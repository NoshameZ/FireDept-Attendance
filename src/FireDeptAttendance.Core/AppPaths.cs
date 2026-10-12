namespace FireDeptAttendance.Core;

/// <summary>
/// Where user data lives. Data is stored in <c>%APPDATA%\FireDeptAttendance</c>
/// (outside the install folder) so application updates never touch it.
/// </summary>
public static class AppPaths
{
    public const string DbFileName = "fire_dept.db";

    /// <summary>Override for tests / portable use via FIREDEPT_DATA_DIR env var.</summary>
    public static string DataDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("FIREDEPT_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FireDeptAttendance");
        }
    }

    public static string DbPath => Path.Combine(DataDir, DbFileName);

    /// <summary>
    /// First-run migration: if the data folder has no database yet, copy
    /// <c>fire_dept.db</c>, <c>settings.json</c> and any <c>signature.*</c> image
    /// from the candidate folders (e.g. next to the exe). Existing files are
    /// never overwritten. Returns a list of the files copied.
    /// </summary>
    public static List<string> MigrateLegacyData(params string[] candidateDirs)
    {
        var copied = new List<string>();
        var dataDir = DataDir;
        Directory.CreateDirectory(dataDir);
        if (File.Exists(Path.Combine(dataDir, DbFileName))) return copied;

        foreach (var dir in candidateDirs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var srcDb = Path.Combine(dir, DbFileName);
                if (!File.Exists(srcDb)) continue;
                if (string.Equals(Path.GetFullPath(dir).TrimEnd('\\', '/'), Path.GetFullPath(dataDir).TrimEnd('\\', '/'),
                        StringComparison.OrdinalIgnoreCase)) continue;

                var files = new List<string> { srcDb };
                var srcSettings = Path.Combine(dir, AppSettings.FileName);
                if (File.Exists(srcSettings)) files.Add(srcSettings);
                files.AddRange(Directory.GetFiles(dir, "signature.*"));
                foreach (var f in files)
                {
                    var dest = Path.Combine(dataDir, Path.GetFileName(f));
                    if (File.Exists(dest)) continue;
                    File.Copy(f, dest);
                    copied.Add(dest);
                }
                break; // first folder with a database wins
            }
            catch (Exception) { /* best effort */ }
        }
        return copied;
    }
}
