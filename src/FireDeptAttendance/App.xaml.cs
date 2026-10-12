using System.IO;
using System.Windows;
using System.Windows.Threading;
using FireDeptAttendance.Core;
using FireDeptAttendance.Dialogs;
using FireDeptAttendance.Services;

namespace FireDeptAttendance;

public partial class App : Application
{
    public static Database Db { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        // Keep the process alive while the (optional) first-run import prompt is shown.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            EnsureDataFolder();
            // Load settings first so the theme applies to the first-run dialogs.
            Settings = AppSettings.Load(AppPaths.DataDir);
            ThemeManager.Apply(Settings);

            // Look for the previous (Python) version's data BEFORE any database is created, so an
            // empty fire_dept.db can never block the migration. Empty candidate DBs are skipped.
            var exeDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            AppPaths.MigrateLegacyData(AppPaths.LegacyCandidateDirs(exeDir).ToArray());
            if (AppPaths.LastMigrationSource != null)
            {
                ReloadSettingsOnly();
                Settings.LastImportFile = AppPaths.LastMigrationSource;
                Settings.Save();
            }

            // Still no records? Ask (until the user explicitly chooses to start empty).
            var current = File.Exists(AppPaths.DbPath) ? DataImport.Inspect(AppPaths.DbPath) : null;
            if (!Settings.SkipImportPrompt && (current is null || (current.IsValid && current.IsEmpty)))
                DataActions.FirstRunPrompt();

            Db = new Database(AppPaths.DbPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the database:\n{ex.Message}\n\nData folder: {AppPaths.DataDir}",
                "Fire Dept Attendance", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var win = new MainWindow();
        MainWindow = win;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        win.Show();
        if (AppPaths.LastMigrationSource != null)
            Ui.Info("Previous data imported",
                $"Your data from the previous version was found and copied into:\n{AppPaths.DataDir}\n\n" +
                $"Source: {AppPaths.LastMigrationSource}\n(The original file was not changed.)");
    }

    /// <summary>
    /// Make sure the data folder is usable. A custom folder that has gone missing (e.g. an unplugged
    /// USB drive) or became read-only offers to fall back to the default folder.
    /// </summary>
    private static void EnsureDataFolder()
    {
        var err = AppPaths.ValidateFolder(AppPaths.DataDir);
        if (err == null) return;
        if (AppPaths.IsEnvOverride || AppPaths.SamePath(AppPaths.DataDir, AppPaths.DefaultDataDir))
            throw new IOException($"The data folder can't be used: {err}");
        var ans = MessageBox.Show(
            $"Your data folder is not available:\n{AppPaths.DataDir}\n\n{err}\n\n" +
            "Click Yes to switch back to the default folder for now:\n" + AppPaths.DefaultDataDir +
            "\n\nClick No to exit (e.g. to reconnect the drive and try again).",
            "Data folder unavailable", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ans != MessageBoxResult.Yes) throw new IOException($"The data folder is not available: {err}");
        AppPaths.ChangeDataDir(null, DataMoveMode.UseExisting);
    }

    /// <summary>Close the open database (before replacing or moving its file).</summary>
    public static void CloseData()
    {
        try { Db?.Dispose(); } catch { /* ignore */ }
        DataImport.ReleaseFileHandles();
    }

    public static void ReloadSettingsOnly()
    {
        Settings = AppSettings.Load(AppPaths.DataDir);
        ThemeManager.Apply(Settings);
    }

    /// <summary>
    /// Re-resolve the data folder, reload settings + database (running migrations) and refresh every tab.
    /// Returns false (after telling the user) if the database can't be opened.
    /// </summary>
    public static bool ReloadData()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            ReloadSettingsOnly();
            Db = new Database(AppPaths.DbPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the database:\n{ex.Message}\n\nData folder: {AppPaths.DataDir}\n\n" +
                            "The app will close. Your backups are in the 'backups' folder inside the data folder.",
                "Fire Dept Attendance", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown(1);
            return false;
        }
        if (Current.MainWindow is MainWindow mw) mw.OnDataReloaded();
        return true;
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Db?.Dispose(); } catch { /* ignore */ }
        base.OnExit(e);
    }
}
