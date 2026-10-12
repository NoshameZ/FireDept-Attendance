using System.IO;
using System.Windows;
using System.Windows.Threading;
using FireDeptAttendance.Core;
using FireDeptAttendance.Services;
using Microsoft.Win32;

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
            Directory.CreateDirectory(AppPaths.DataDir);
            // First run: copy fire_dept.db / settings.json / signature.* from next to the exe
            // (or its parent folder, e.g. the Velopack install root) into %APPDATA%\FireDeptAttendance.
            var exeDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            var parent = Path.GetDirectoryName(exeDir) ?? "";
            AppPaths.MigrateLegacyData(exeDir, parent);
            if (!File.Exists(AppPaths.DbPath)) OfferImport();

            Settings = AppSettings.Load(AppPaths.DataDir);
            ThemeManager.Apply(Settings);
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
    }

    /// <summary>
    /// No database in the data folder yet: offer to import one from the previous
    /// (Python) version, so existing members, events and settings carry over.
    /// </summary>
    private static void OfferImport()
    {
        var answer = MessageBox.Show(
            "Welcome! No existing attendance data was found.\n\n" +
            "If you used the previous version of this app, click Yes and select its fire_dept.db file " +
            "to import your members, events and settings.\n\nClick No to start with an empty database.",
            "Import existing data?", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        var dlg = new OpenFileDialog
        {
            Title = "Select your existing fire_dept.db",
            Filter = "Attendance database (fire_dept.db)|fire_dept.db|SQLite database (*.db)|*.db|All files (*.*)|*.*",
            FileName = AppPaths.DbFileName,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var dir = Path.GetDirectoryName(dlg.FileName)!;
            if (string.Equals(Path.GetFileName(dlg.FileName), AppPaths.DbFileName, StringComparison.OrdinalIgnoreCase))
            {
                AppPaths.MigrateLegacyData(dir);
            }
            else
            {
                File.Copy(dlg.FileName, AppPaths.DbPath, overwrite: false);
                var s = Path.Combine(dir, AppSettings.FileName);
                if (File.Exists(s)) File.Copy(s, Path.Combine(AppPaths.DataDir, AppSettings.FileName), false);
                foreach (var sig in Directory.GetFiles(dir, "signature.*"))
                {
                    var dest = Path.Combine(AppPaths.DataDir, Path.GetFileName(sig));
                    if (!File.Exists(dest)) File.Copy(sig, dest);
                }
            }
            if (File.Exists(AppPaths.DbPath))
                MessageBox.Show($"Your data was imported into:\n{AppPaths.DataDir}", "Import complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not import that file:\n{ex.Message}", "Import failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
