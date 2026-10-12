using System.Text.Json;
using System.Text.Json.Nodes;

namespace FireDeptAttendance.Core;

/// <summary>How existing data is handled when the data folder is changed.</summary>
public enum DataMoveMode
{
    /// <summary>Copy the data files to the new folder, then delete them from the old one.</summary>
    Move,
    /// <summary>Copy the data files to the new folder and leave the originals in place.</summary>
    Copy,
    /// <summary>Don't copy anything; use whatever data is already in the new folder.</summary>
    UseExisting,
}

/// <summary>
/// Where user data lives. By default <c>%APPDATA%\FireDeptAttendance</c> (outside the
/// install folder, so updates never touch it). The user can choose another folder; the
/// choice is stored in a small pointer file <c>location.json</c> in the default folder.
/// Resolution order: <c>FIREDEPT_DATA_DIR</c> env var → pointer file → default.
/// </summary>
public static class AppPaths
{
    public const string DbFileName = "fire_dept.db";
    public const string PointerFileName = "location.json";
    public const string BackupsFolderName = "backups";

    /// <summary>Test hook: replaces %APPDATA%\FireDeptAttendance as the default/pointer folder.</summary>
    public static string? DefaultDirOverride { get; set; }

    /// <summary>The built-in default data folder (also where the pointer file lives).</summary>
    public static string DefaultDataDir =>
        !string.IsNullOrWhiteSpace(DefaultDirOverride)
            ? DefaultDirOverride!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FireDeptAttendance");

    public static string PointerPath => Path.Combine(DefaultDataDir, PointerFileName);

    /// <summary>True when FIREDEPT_DATA_DIR is set (the folder setting is then locked).</summary>
    public static bool IsEnvOverride => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIREDEPT_DATA_DIR"));

    public static string DataDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("FIREDEPT_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return ReadPointer() ?? DefaultDataDir;
        }
    }

    public static string DbPath => Path.Combine(DataDir, DbFileName);
    public static string BackupsDir => Path.Combine(DataDir, BackupsFolderName);

    /// <summary>Custom folder from the pointer file, or null if none / unreadable.</summary>
    public static string? ReadPointer()
    {
        try
        {
            if (!File.Exists(PointerPath)) return null;
            var node = JsonNode.Parse(File.ReadAllText(PointerPath)) as JsonObject;
            var dir = node?["data_dir"] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
            return dir.Length > 0 && Path.IsPathRooted(dir) ? dir : null;
        }
        catch (Exception) { return null; }
    }

    private static void WritePointer(string? dir)
    {
        Directory.CreateDirectory(DefaultDataDir);
        if (dir is null || SamePath(dir, DefaultDataDir))
        {
            if (File.Exists(PointerPath)) File.Delete(PointerPath);
            return;
        }
        var tmp = PointerPath + ".tmp";
        File.WriteAllText(tmp, new JsonObject { ["data_dir"] = Path.GetFullPath(dir) }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, PointerPath, overwrite: true);
    }

    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Returns null if <paramref name="dir"/> can be used as a data folder (creating it if
    /// needed and test-writing a file), otherwise a user-readable reason.
    /// </summary>
    public static string? ValidateFolder(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "No folder was selected.";
        try
        {
            if (!Path.IsPathRooted(dir)) return "Please choose a full folder path (for example C:\\Data\\FireDept).";
            if (File.Exists(dir)) return "That path is a file, not a folder.";
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write_test_{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (UnauthorizedAccessException) { return "You don't have permission to write to that folder."; }
        catch (Exception ex) { return $"That folder can't be used: {ex.Message}"; }
    }

    /// <summary>Data files belonging to the app in a folder: db (+ journal/wal/shm), settings, signature.*.</summary>
    public static List<string> DataFilesIn(string dir)
    {
        var files = new List<string>();
        if (!Directory.Exists(dir)) return files;
        foreach (var name in new[] { DbFileName, DbFileName + "-wal", DbFileName + "-shm", DbFileName + "-journal", AppSettings.FileName })
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) files.Add(p);
        }
        files.AddRange(Directory.GetFiles(dir, "signature.*"));
        return files;
    }

    /// <summary>
    /// Switch the data folder to <paramref name="newDir"/> (null = back to default).
    /// Move/Copy bring the db, settings.json, signature.* and backups along; existing files
    /// in the destination are never overwritten (the call fails instead, nothing is changed).
    /// The caller must close the open database first. Returns the new data folder.
    /// </summary>
    public static string ChangeDataDir(string? newDir, DataMoveMode mode)
    {
        if (IsEnvOverride)
            throw new InvalidOperationException("The data folder is fixed by the FIREDEPT_DATA_DIR environment variable.");
        var target = string.IsNullOrWhiteSpace(newDir) ? DefaultDataDir : Path.GetFullPath(newDir!);
        var err = ValidateFolder(target);
        if (err != null) throw new IOException(err);
        var oldDir = DataDir;
        if (SamePath(oldDir, target)) { WritePointer(target); return target; }

        if (mode != DataMoveMode.UseExisting)
        {
            var files = DataFilesIn(oldDir);
            var oldBackups = Path.Combine(oldDir, BackupsFolderName);
            var backupFiles = Directory.Exists(oldBackups) ? Directory.GetFiles(oldBackups) : Array.Empty<string>();
            var conflicts = files.Select(f => Path.Combine(target, Path.GetFileName(f)))
                .Concat(backupFiles.Select(f => Path.Combine(target, BackupsFolderName, Path.GetFileName(f))))
                .Where(File.Exists).ToList();
            if (conflicts.Count > 0)
                throw new IOException("The new folder already contains attendance data (" +
                                      string.Join(", ", conflicts.Select(Path.GetFileName).Distinct()) +
                                      "). Choose \"Use the data already there\" or pick an empty folder.");

            var copied = new List<string>();
            try
            {
                foreach (var f in files)
                {
                    var dest = Path.Combine(target, Path.GetFileName(f));
                    File.Copy(f, dest);
                    copied.Add(dest);
                }
                if (backupFiles.Length > 0)
                {
                    Directory.CreateDirectory(Path.Combine(target, BackupsFolderName));
                    foreach (var f in backupFiles)
                    {
                        var dest = Path.Combine(target, BackupsFolderName, Path.GetFileName(f));
                        File.Copy(f, dest);
                        copied.Add(dest);
                    }
                }
            }
            catch (Exception)
            {
                foreach (var c in copied) { try { File.Delete(c); } catch (Exception) { /* best effort */ } }
                throw;
            }

            if (mode == DataMoveMode.Move)
            {
                foreach (var f in files.Concat(backupFiles))
                {
                    try { File.Delete(f); } catch (Exception) { /* leave originals if locked */ }
                }
                try
                {
                    if (Directory.Exists(oldBackups) && !Directory.EnumerateFileSystemEntries(oldBackups).Any())
                        Directory.Delete(oldBackups);
                }
                catch (Exception) { /* ignore */ }
            }
        }

        WritePointer(target);
        return target;
    }

    /// <summary>Folders where the previous (Python) version may have stored fire_dept.db.</summary>
    public static List<string> LegacyCandidateDirs(string exeDir)
    {
        var list = new List<string>();
        void Add(string? d) { if (!string.IsNullOrWhiteSpace(d)) list.Add(d!); }
        Add(exeDir);
        Add(Path.GetDirectoryName(exeDir.TrimEnd('\\', '/')));
        foreach (var sf in new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop })
        {
            var root = Environment.GetFolderPath(sf);
            if (string.IsNullOrEmpty(root)) continue;
            foreach (var name in new[] { "FireDeptAttendance", "fire_dept_attendance", "Fire Dept Attendance", "FireDept-Attendance" })
            {
                Add(Path.Combine(root, name));
                Add(Path.Combine(root, name, "dist"));
            }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            foreach (var name in new[] { "FireDeptAttendance", "fire_dept_attendance" })
                Add(Path.Combine(home, "Downloads", name));
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The fire_dept.db imported by the last successful <see cref="MigrateLegacyData"/> call.</summary>
    public static string? LastMigrationSource { get; private set; }

    /// <summary>
    /// First-run migration. Runs BEFORE the database is opened/created. If the data folder
    /// has no database — or only an empty one (no members, no events) — the first candidate
    /// folder whose fire_dept.db actually contains data is imported via the SQLite backup
    /// API (so -wal/-journal content is included), along with settings.json and signature.*.
    /// Returns the files written (empty if nothing was imported).
    /// </summary>
    public static List<string> MigrateLegacyData(params string[] candidateDirs)
    {
        LastMigrationSource = null;
        var copied = new List<string>();
        var dataDir = DataDir;
        Directory.CreateDirectory(dataDir);
        var destDb = Path.Combine(dataDir, DbFileName);
        if (File.Exists(destDb))
        {
            var cur = DataImport.Inspect(destDb);
            if (!cur.IsValid || !cur.IsEmpty) return copied; // real data already here
        }

        foreach (var dir in candidateDirs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct())
        {
            try
            {
                if (!Directory.Exists(dir) || SamePath(dir, dataDir)) continue;
                var srcDb = Path.Combine(dir, DbFileName);
                if (!File.Exists(srcDb)) continue;
                var info = DataImport.Inspect(srcDb);
                if (!info.IsValid || info.IsEmpty) continue; // skip empty/invalid DBs, keep looking
                copied.AddRange(DataImport.Import(srcDb, dataDir, includeSideFiles: true, overwriteSideFiles: false,
                    backupCurrent: false).FilesWritten);
                LastMigrationSource = srcDb;
                break; // first folder with real data wins
            }
            catch (Exception) { /* best effort */ }
        }
        return copied;
    }
}
