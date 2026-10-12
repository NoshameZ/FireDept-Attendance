using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace FireDeptAttendance.Core;

/// <summary>Summary of an attendance database, shown before importing/restoring.</summary>
public sealed record DbPreview(
    string Path,
    bool IsValid,
    string Error,
    long Members,
    long ActiveMembers,
    long Events,
    long Meetings,
    long Calls,
    long Quarters,
    long AttendanceMarks,
    string FirstEventDate,
    string LastEventDate)
{
    public bool IsEmpty => Members == 0 && Events == 0;

    public static DbPreview Invalid(string path, string error) =>
        new(path, false, error, 0, 0, 0, 0, 0, 0, 0, "", "");

    /// <summary>Multi-line human readable description.</summary>
    public string Describe()
    {
        if (!IsValid) return Error;
        var sb = new StringBuilder();
        sb.AppendLine($"Members: {Members} ({ActiveMembers} active)");
        sb.AppendLine($"Events: {Events} ({Meetings} meetings, {Calls} calls)");
        sb.AppendLine($"Quarters: {Quarters}");
        sb.AppendLine($"Attendance marks: {AttendanceMarks}");
        sb.Append(Events > 0 ? $"Date range: {FirstEventDate} to {LastEventDate}" : "Date range: (no events)");
        return sb.ToString();
    }
}

public sealed record ImportResult(string? BackupPath, List<string> FilesWritten, DbPreview Imported);

/// <summary>
/// Importing / backing up / restoring whole attendance databases. All copies go through the
/// SQLite online-backup API on a private staged copy of the source, so committed data that
/// still sits in a <c>-wal</c> or hot <c>-journal</c> file is included and the source file
/// itself is never modified. The app's own database must be closed while replacing.
/// </summary>
public static class DataImport
{
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    /// <summary>Release any pooled SQLite file handles (call after closing the app database).</summary>
    public static void ReleaseFileHandles() => SqliteConnection.ClearAllPools();

    private static string ConnString(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = mode, Pooling = false,
    }.ToString();

    /// <summary>Copy db + -wal + -journal into a fresh temp folder (never touches the source).</summary>
    private static string Stage(string srcDb, string? parentDir = null)
    {
        var dir = System.IO.Path.Combine(parentDir ?? System.IO.Path.GetTempPath(), $".fd_import_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var staged = System.IO.Path.Combine(dir, AppPaths.DbFileName);
        File.Copy(srcDb, staged);
        foreach (var suffix in new[] { "-wal", "-journal" })
            if (File.Exists(srcDb + suffix)) File.Copy(srcDb + suffix, staged + suffix);
        return staged;
    }

    private static void Unstage(string staged)
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(System.IO.Path.GetDirectoryName(staged)!, recursive: true); }
        catch (Exception) { /* best effort */ }
    }

    /// <summary>Validate a file as an attendance database and count what's in it.</summary>
    public static DbPreview Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return DbPreview.Invalid(path, "The file does not exist.");
        try
        {
            var head = new byte[16];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length == 0) return DbPreview.Invalid(path, "The file is empty (0 bytes).");
                if (fs.Read(head, 0, 16) < 16 || !head.SequenceEqual(SqliteHeader))
                    return DbPreview.Invalid(path, "This is not a SQLite database file.");
            }
        }
        catch (Exception ex) { return DbPreview.Invalid(path, $"Could not read the file: {ex.Message}"); }

        string staged;
        try { staged = Stage(path); }
        catch (Exception ex) { return DbPreview.Invalid(path, $"Could not read the file: {ex.Message}"); }
        try
        {
            using var conn = new SqliteConnection(ConnString(staged, SqliteOpenMode.ReadWrite));
            conn.Open();
            long Scalar(string sql)
            {
                using var c = conn.CreateCommand();
                c.CommandText = sql;
                var v = c.ExecuteScalar();
                return v is null or DBNull ? 0 : Convert.ToInt64(v);
            }
            string Str(string sql)
            {
                using var c = conn.CreateCommand();
                c.CommandText = sql;
                return c.ExecuteScalar() as string ?? "";
            }
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                using var r = c.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }
            if (!tables.Contains("members") || !tables.Contains("events"))
                return DbPreview.Invalid(path,
                    "This SQLite file is not a Fire Dept Attendance database (no members/events tables).");

            var hasQuarters = tables.Contains("quarters");
            var hasAttendance = tables.Contains("attendance");
            return new DbPreview(path, true, "",
                Scalar("SELECT COUNT(*) FROM members"),
                Scalar("SELECT COUNT(*) FROM members WHERE active = 1"),
                Scalar("SELECT COUNT(*) FROM events"),
                Scalar("SELECT COUNT(*) FROM events WHERE event_type = 'meeting'"),
                Scalar("SELECT COUNT(*) FROM events WHERE event_type <> 'meeting'"),
                hasQuarters ? Scalar("SELECT COUNT(*) FROM quarters") : 0,
                hasAttendance ? Scalar("SELECT COUNT(*) FROM attendance WHERE present = 1") : 0,
                Str("SELECT MIN(event_date) FROM events"),
                Str("SELECT MAX(event_date) FROM events"));
        }
        catch (Exception ex)
        {
            return DbPreview.Invalid(path, $"The database could not be opened: {ex.Message}");
        }
        finally { Unstage(staged); }
    }

    /// <summary>Copy <paramref name="srcDb"/> into <paramref name="destDb"/> via the backup API.</summary>
    private static void BackupApiCopy(string srcDb, string destDb)
    {
        var staged = Stage(srcDb);
        try
        {
            using var src = new SqliteConnection(ConnString(staged, SqliteOpenMode.ReadWrite));
            src.Open();
            using var dst = new SqliteConnection(ConnString(destDb, SqliteOpenMode.ReadWriteCreate));
            dst.Open();
            src.BackupDatabase(dst);
        }
        finally { Unstage(staged); }
        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Save a timestamped copy of the data folder's current database to
    /// <c>backups\fire_dept_yyyyMMdd_HHmmss.db</c>. Returns its path, or null if there is no database.
    /// </summary>
    public static string? BackupCurrent(string dataDir, string label = "")
    {
        var db = System.IO.Path.Combine(dataDir, AppPaths.DbFileName);
        if (!File.Exists(db)) return null;
        var dir = System.IO.Path.Combine(dataDir, AppPaths.BackupsFolderName);
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var suffix = string.IsNullOrEmpty(label) ? "" : "_" + label;
        var dest = System.IO.Path.Combine(dir, $"fire_dept_{stamp}{suffix}.db");
        for (var i = 2; File.Exists(dest); i++)
            dest = System.IO.Path.Combine(dir, $"fire_dept_{stamp}{suffix}_{i}.db");
        BackupApiCopy(db, dest);
        return dest;
    }

    /// <summary>Backups in the data folder, newest first.</summary>
    public static List<string> ListBackups(string dataDir)
    {
        var dir = System.IO.Path.Combine(dataDir, AppPaths.BackupsFolderName);
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.GetFiles(dir, "*.db").OrderByDescending(File.GetLastWriteTimeUtc).ToList();
    }

    /// <summary>
    /// Replace the data folder's database with <paramref name="srcDb"/>.
    /// <list type="number">
    /// <item>validates the source,</item>
    /// <item>backs up the current database (if any) into <c>backups\</c>,</item>
    /// <item>copies the source via the backup API into a temp file, runs the schema migrations on it,</item>
    /// <item>swaps it into place (removing stale -wal/-shm/-journal files).</item>
    /// </list>
    /// With <paramref name="includeSideFiles"/>, settings.json and signature.* next to the source
    /// are imported too (the destination's "paths" settings are kept).
    /// The app's open <see cref="Database"/> must be disposed before calling this.
    /// </summary>
    public static ImportResult Import(string srcDb, string dataDir, bool includeSideFiles, bool overwriteSideFiles = true,
        bool backupCurrent = true, string backupLabel = "before_import")
    {
        var preview = Inspect(srcDb);
        if (!preview.IsValid) throw new InvalidDataException(preview.Error);
        Directory.CreateDirectory(dataDir);
        var destDb = System.IO.Path.Combine(dataDir, AppPaths.DbFileName);
        if (AppPaths.SamePath(srcDb, destDb))
            throw new InvalidOperationException("That file is already the database currently in use.");

        SqliteConnection.ClearAllPools();
        string? backup = backupCurrent ? BackupCurrent(dataDir, backupLabel) : null;
        var written = new List<string>();

        var tmp = System.IO.Path.Combine(dataDir, $".import_{Guid.NewGuid():N}.db");
        try
        {
            BackupApiCopy(srcDb, tmp);
            using (new Database(tmp)) { } // run schema migrations on the copy before it goes live
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                if (File.Exists(destDb + suffix)) File.Delete(destDb + suffix);
            File.Move(tmp, destDb, overwrite: true);
            written.Add(destDb);
        }
        finally
        {
            foreach (var f in new[] { tmp, tmp + "-wal", tmp + "-shm", tmp + "-journal" })
                try { if (File.Exists(f)) File.Delete(f); } catch (Exception) { /* ignore */ }
        }

        if (includeSideFiles)
        {
            var srcDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(srcDb))!;
            if (!AppPaths.SamePath(srcDir, dataDir))
            {
                var srcSettings = System.IO.Path.Combine(srcDir, AppSettings.FileName);
                var destSettings = System.IO.Path.Combine(dataDir, AppSettings.FileName);
                if (File.Exists(srcSettings) && (overwriteSideFiles || !File.Exists(destSettings)))
                {
                    if (CopySettingsKeepingPaths(srcSettings, destSettings)) written.Add(destSettings);
                }
                foreach (var sig in Directory.GetFiles(srcDir, "signature.*"))
                {
                    var dest = System.IO.Path.Combine(dataDir, System.IO.Path.GetFileName(sig));
                    if (File.Exists(dest) && !overwriteSideFiles) continue;
                    File.Copy(sig, dest, overwrite: true);
                    written.Add(dest);
                }
            }
        }
        return new ImportResult(backup, written, Inspect(destDb));
    }

    /// <summary>Restore a backup (the current database is itself backed up first).</summary>
    public static ImportResult RestoreBackup(string backupPath, string dataDir) =>
        Import(backupPath, dataDir, includeSideFiles: false, backupCurrent: true, backupLabel: "before_restore");

    /// <summary>Copy a settings.json, preserving the destination's "paths" section (folder preferences).</summary>
    private static bool CopySettingsKeepingPaths(string src, string dest)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(src)) as JsonObject;
            if (root is null) return false;
            if (File.Exists(dest) && JsonNode.Parse(File.ReadAllText(dest)) is JsonObject cur && cur["paths"] is JsonNode p)
                root["paths"] = p.DeepClone();
            var tmp = dest + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, dest, overwrite: true);
            return true;
        }
        catch (Exception) { return false; }
    }
}
