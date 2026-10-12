using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FireDeptAttendance.Core;
using FireDeptAttendance.Services;
using Microsoft.Win32;

namespace FireDeptAttendance.Dialogs;

/// <summary>Small modal "pick one of these options" dialog. Returns the chosen index, or -1 if cancelled.</summary>
public sealed class ChoiceDialog : Window
{
    private int _result = -1;

    private ChoiceDialog(Window? owner, string title, string message, IReadOnlyList<string> options, int defaultIndex)
    {
        Ui.SetupDialog(this, title, owner, 520, 300, resizable: false);
        SizeToContent = SizeToContent.Height;
        var (root, btns) = DialogLayout.Make();
        var radios = new List<RadioButton>();
        var sp = new StackPanel();
        sp.Children.Add(Ui.M(Ui.Text(message, Lbl.Normal, true), 0, 0, 0, 12));
        for (var i = 0; i < options.Count; i++)
        {
            var rb = new RadioButton
            {
                Content = new TextBlock { Text = options[i], TextWrapping = TextWrapping.Wrap },
                IsChecked = i == defaultIndex, Margin = new Thickness(0, 3, 0, 3),
            };
            radios.Add(rb);
            sp.Children.Add(rb);
        }
        DialogLayout.Add(btns, Ui.Button("Cancel", Close, "GhostButton"), Dock.Right);
        DialogLayout.Add(btns, Ui.Button("Continue", () =>
        {
            _result = radios.FindIndex(r => r.IsChecked == true);
            Close();
        }, "AccentButton"), Dock.Right);
        root.Children.Add(sp);
        Content = root;
    }

    public static int Ask(Window? owner, string title, string message, IReadOnlyList<string> options, int defaultIndex = 0)
    {
        var d = new ChoiceDialog(owner, title, message, options, defaultIndex);
        d.ShowDialog();
        return d._result;
    }
}

/// <summary>
/// Data-folder / import / backup operations shared by the Data &amp; Storage dialog, the Export tab
/// and the first-run prompt. Every operation that swaps the database closes it first and reopens
/// (with migrations) afterwards via <see cref="App.ReloadData"/>.
/// </summary>
public static class DataActions
{
    public static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Ui.Error("Open folder", $"Could not open:\n{path}\n\n{ex.Message}"); }
    }

    /// <summary>Show a file picker for a fire_dept.db (starting at the last import location).</summary>
    public static string? PickDatabaseFile(Window? owner, string title = "Select the fire_dept.db to import")
    {
        var s = App.Settings;
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "Attendance database (*.db)|*.db|All files (*.*)|*.*",
            FileName = AppPaths.DbFileName,
            InitialDirectory = AppSettings.ResolveDir(s?.LastImportDir),
            CheckFileExists = true,
        };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    /// <summary>
    /// Validate + preview + confirm. Returns (ok, includeSideFiles). Shared by the in-app import and the
    /// first-run prompt.
    /// </summary>
    public static (bool Ok, bool SideFiles) ConfirmImport(Window? owner, string path, DbPreview p, bool replacingExisting)
    {
        if (!p.IsValid)
        {
            Ui.Error("Not an attendance database", $"{path}\n\n{p.Error}");
            return (false, false);
        }
        if (p.IsEmpty &&
            !Ui.Confirm("Database is empty",
                $"{path}\n\nThis database contains NO members and NO events — it is probably not the file with your " +
                "records (for example an empty copy that sat next to a freshly built program).\n\nImport it anyway?"))
            return (false, false);

        var msg = $"Import this database?\n\n{path}\n\n{p.Describe()}\n\n";
        msg += replacingExisting
            ? "Your current data will be saved to the backups folder first, then replaced by this database " +
              "(use \"Restore previous backup\" to undo)."
            : "It will be copied into your data folder. The original file is not changed.";
        if (!Ui.Confirm("Confirm import", msg, MessageBoxImage.Question)) return (false, false);

        var dir = Path.GetDirectoryName(path) ?? "";
        var side = new List<string>();
        if (File.Exists(Path.Combine(dir, AppSettings.FileName))) side.Add(AppSettings.FileName);
        if (Directory.Exists(dir)) side.AddRange(Directory.GetFiles(dir, "signature.*").Select(Path.GetFileName)!);
        if (side.Count == 0 || AppPaths.SamePath(dir, AppPaths.DataDir)) return (true, false);
        var text =
            $"The same folder also contains: {string.Join(", ", side)}.\n\n" +
            "Import these too (settings such as rates, default locations, minutes header/footer and the signature image)?\n\n" +
            "Yes = import data + settings/signature\nNo = import the database only";
        var ans = owner is { IsVisible: true }
            ? MessageBox.Show(owner, text, "Import settings too?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question)
            : MessageBox.Show(text, "Import settings too?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return ans switch
        {
            MessageBoxResult.Yes => (true, true),
            MessageBoxResult.No => (true, false),
            _ => (false, false),
        };
    }

    /// <summary>Import a database chosen by the user (or <paramref name="path"/> for "Re-import").</summary>
    public static bool ImportFromDatabase(Window? owner, string? path = null)
    {
        path ??= PickDatabaseFile(owner);
        if (path is null) return false;
        if (!File.Exists(path)) { Ui.Error("Import", $"File not found:\n{path}"); return false; }
        if (AppPaths.SamePath(path, AppPaths.DbPath))
        {
            Ui.Info("Import", "That file is the database the app is already using.");
            return false;
        }
        var preview = DataImport.Inspect(path);
        var (ok, side) = ConfirmImport(owner, path, preview, replacingExisting: true);
        if (!ok) return false;

        ImportResult? result = null;
        Exception? error = null;
        App.CloseData();
        try { result = DataImport.Import(path, AppPaths.DataDir, includeSideFiles: side); }
        catch (Exception ex) { error = ex; }
        if (!App.ReloadData()) return false;

        if (error != null)
        {
            Ui.Error("Import failed", $"Nothing was changed.\n\n{error.Message}");
            return false;
        }
        App.Settings.LastImportFile = path;
        App.Settings.Save();
        var msg = $"Imported {result!.Imported.Members} members and {result.Imported.Events} events into:\n{AppPaths.DataDir}";
        if (result.BackupPath != null) msg += $"\n\nYour previous data was backed up to:\n{result.BackupPath}";
        Ui.Info("Import complete", msg);
        return true;
    }

    public static bool RestoreBackup(Window? owner)
    {
        var backups = DataImport.ListBackups(AppPaths.DataDir);
        if (backups.Count == 0)
        {
            Ui.Info("Restore backup", $"No backups yet. A backup is saved automatically before every import or restore, in:\n{AppPaths.BackupsDir}");
            return false;
        }
        var dlg = new OpenFileDialog
        {
            Title = "Choose a backup to restore",
            Filter = "Database backups (*.db)|*.db",
            InitialDirectory = AppPaths.BackupsDir,
            FileName = Path.GetFileName(backups[0]),
        };
        if (dlg.ShowDialog(owner) != true) return false;
        var p = DataImport.Inspect(dlg.FileName);
        if (!p.IsValid) { Ui.Error("Not an attendance database", p.Error); return false; }
        if (!Ui.Confirm("Restore backup",
                $"Restore this backup?\n\n{dlg.FileName}\nSaved {File.GetLastWriteTime(dlg.FileName):g}\n\n{p.Describe()}\n\n" +
                "Your current data will itself be backed up first.", MessageBoxImage.Question))
            return false;

        ImportResult? result = null;
        Exception? error = null;
        App.CloseData();
        try { result = DataImport.RestoreBackup(dlg.FileName, AppPaths.DataDir); }
        catch (Exception ex) { error = ex; }
        if (!App.ReloadData()) return false;
        if (error != null) { Ui.Error("Restore failed", $"Nothing was changed.\n\n{error.Message}"); return false; }
        Ui.Info("Backup restored", $"Restored {result!.Imported.Members} members and {result.Imported.Events} events." +
                                   (result.BackupPath != null ? $"\n\nThe data you had before was saved to:\n{result.BackupPath}" : ""));
        return true;
    }

    /// <summary>Change (or reset, when <paramref name="newDir"/> is null) the data folder.</summary>
    public static bool ChangeDataFolder(Window? owner, string? newDir)
    {
        if (AppPaths.IsEnvOverride)
        {
            Ui.Warn("Data folder", "The data folder is set by the FIREDEPT_DATA_DIR environment variable and can't be changed here.");
            return false;
        }
        var target = newDir ?? AppPaths.DefaultDataDir;
        if (AppPaths.SamePath(target, AppPaths.DataDir))
        {
            Ui.Info("Data folder", "That is already the current data folder.");
            return false;
        }
        var err = AppPaths.ValidateFolder(target);
        if (err != null) { Ui.Error("Can't use that folder", $"{target}\n\n{err}"); return false; }

        var targetDb = Path.Combine(target, AppPaths.DbFileName);
        var there = File.Exists(targetDb) ? DataImport.Inspect(targetDb) : null;
        var current = DataImport.Inspect(AppPaths.DbPath);
        var desc = there is { IsValid: true }
            ? $"The new folder already contains a database:\n{there.Describe()}"
            : "The new folder has no attendance database yet.";
        var options = new List<string>
        {
            "Move my data there (copy, then remove it from the old folder)",
            "Copy my data there (keep the old folder as it is)",
            "Use the data already in the new folder" + (there is { IsValid: true } ? "" : " (starts empty)"),
        };
        var def = there is { IsValid: true, IsEmpty: false } && (current.IsEmpty || !current.IsValid) ? 2 : 0;
        var pick = ChoiceDialog.Ask(owner, "Change data folder",
            $"New data folder:\n{target}\n\n{desc}\n\nCurrent data ({AppPaths.DataDir}):\n{current.Describe()}\n\n" +
            "What should happen to your current data?", options, def);
        if (pick < 0) return false;
        var mode = pick switch { 0 => DataMoveMode.Move, 1 => DataMoveMode.Copy, _ => DataMoveMode.UseExisting };

        Exception? error = null;
        App.CloseData();
        try { AppPaths.ChangeDataDir(newDir, mode); }
        catch (Exception ex) { error = ex; }
        if (!App.ReloadData()) return false;
        if (error != null) { Ui.Error("Data folder not changed", error.Message); return false; }
        Ui.Info("Data folder changed", $"The app now stores its data in:\n{AppPaths.DataDir}");
        return true;
    }

    /// <summary>
    /// First-run / still-empty prompt, shown at startup BEFORE the database is opened. Returns normally in
    /// every case; the caller then opens whatever database is in the data folder.
    /// </summary>
    public static void FirstRunPrompt()
    {
        while (true)
        {
            var pick = ChoiceDialog.Ask(null, "Import your existing data?",
                "Welcome! The data folder has no attendance records yet:\n" + AppPaths.DataDir + "\n\n" +
                "If you used the previous version of this app, select its fire_dept.db to bring over your members, " +
                "events, attendance and settings. (The old app kept fire_dept.db in the same folder as its program " +
                "file — app.py or the .exe.)",
                new[]
                {
                    "Find my previous fire_dept.db…",
                    "Start with an empty database (don't ask again — you can import later from Settings → Data & Storage)",
                    "Decide later (ask again next time)",
                });
            if (pick == 1)
            {
                App.Settings.SkipImportPrompt = true;
                App.Settings.Save();
                return;
            }
            if (pick != 0) return;

            var path = PickDatabaseFile(null);
            if (path is null) continue;
            var preview = DataImport.Inspect(path);
            var existing = File.Exists(AppPaths.DbPath);
            var (ok, side) = ConfirmImport(null, path, preview, replacingExisting: existing);
            if (!ok) continue;
            try
            {
                DataImport.Import(path, AppPaths.DataDir, includeSideFiles: side, backupCurrent: existing);
                App.ReloadSettingsOnly();
                App.Settings.LastImportFile = path;
                App.Settings.Save();
                Ui.Info("Import complete", $"Imported {preview.Members} members and {preview.Events} events into:\n{AppPaths.DataDir}");
                return;
            }
            catch (Exception ex)
            {
                Ui.Error("Import failed", $"{ex.Message}\n\nPlease choose another file.");
            }
        }
    }
}

/// <summary>Settings → Data &amp; Storage: data folder, import/restore, export folders, install location.</summary>
public sealed class DataDialog : Window
{
    private readonly ContentControl _body = new();

    public DataDialog(Window? owner)
    {
        Ui.SetupDialog(this, "Data & Storage", owner, 640, 700);
        MinWidth = 520; MinHeight = 420;
        var (root, btns) = DialogLayout.Make();
        DialogLayout.Add(btns, Ui.Button("Close", Close, "AccentButton"), Dock.Right);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body });
        Content = root;
        Build();
    }

    private static TextBox PathBox(string text) => new()
    {
        Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6),
    };

    private static WrapPanel Row(params UIElement[] items)
    {
        var w = new WrapPanel();
        foreach (var i in items)
        {
            if (i is FrameworkElement fe) fe.Margin = new Thickness(0, 2, 8, 2);
            w.Children.Add(i);
        }
        return w;
    }

    private void Run(Func<bool> action)
    {
        action();
        Build();
    }

    private void Build()
    {
        var s = App.Settings;
        var sp = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        sp.Children.Add(Ui.M(Ui.Text("Data & Storage", Lbl.Header), 0, 0, 0, 2));
        sp.Children.Add(Ui.Text("Where your attendance records live, importing old data, and default save folders.", Lbl.Sub, true));

        // ---- data folder ----
        var df = new StackPanel();
        df.Children.Add(Ui.Text("The database (fire_dept.db), settings.json, the signature image and backups are all kept here. " +
                                "App updates never touch this folder.", Lbl.Sub, true));
        df.Children.Add(PathBox(AppPaths.DataDir));
        var cur = DataImport.Inspect(AppPaths.DbPath);
        df.Children.Add(Ui.Text(cur.IsValid
            ? $"Contains {cur.Members} members, {cur.Events} events, {cur.Quarters} quarters."
            : "No database yet.", Lbl.Sub));
        var change = Ui.Button("📁  Change…", () => Run(() =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose the folder to store attendance data in", InitialDirectory = AppPaths.DataDir };
            return dlg.ShowDialog(this) == true && DataActions.ChangeDataFolder(this, dlg.FolderName);
        }), "GhostButton");
        var reset = Ui.Button("Reset to default", () => Run(() => DataActions.ChangeDataFolder(this, null)), "GhostButton");
        if (AppPaths.IsEnvOverride)
        {
            change.IsEnabled = reset.IsEnabled = false;
            df.Children.Add(Ui.Text("Locked by the FIREDEPT_DATA_DIR environment variable.", Lbl.Sub, true));
        }
        else if (AppPaths.SamePath(AppPaths.DataDir, AppPaths.DefaultDataDir)) reset.IsEnabled = false;
        df.Children.Add(Row(change, Ui.Button("Open folder", () => DataActions.OpenFolder(AppPaths.DataDir), "GhostButton"), reset));
        df.Children.Add(Ui.Text($"Default: {AppPaths.DefaultDataDir}", Lbl.Small, true));
        sp.Children.Add(Ui.Group("Data folder", df));

        // ---- import / backups ----
        var im = new StackPanel();
        im.Children.Add(Ui.Text("Bring in members, events and attendance from another fire_dept.db — e.g. the previous version " +
                                "of this app, which kept fire_dept.db next to its program file (app.py or the .exe). " +
                                "You'll see what's in the file before anything changes, and your current data is backed up first.",
            Lbl.Sub, true));
        im.Children.Add(Ui.M(Ui.Text("Last import location:", Lbl.FieldLbl), 0, 6, 0, 0));
        var last = s.LastImportFile;
        im.Children.Add(PathBox(string.IsNullOrEmpty(last) ? "(none yet)" : last));
        var reimport = Ui.Button("⟳  Re-import from there", () => Run(() => DataActions.ImportFromDatabase(this, last)), "GhostButton");
        reimport.IsEnabled = !string.IsNullOrEmpty(last) && File.Exists(last);
        var backups = DataImport.ListBackups(AppPaths.DataDir);
        var restore = Ui.Button("↶  Restore previous backup…", () => Run(() => DataActions.RestoreBackup(this)), "GhostButton");
        restore.IsEnabled = backups.Count > 0;
        im.Children.Add(Row(
            Ui.Button("📥  Import from database…", () => Run(() => DataActions.ImportFromDatabase(this)), "AccentButton"),
            reimport, restore));
        im.Children.Add(Ui.Text(backups.Count > 0
            ? $"{backups.Count} backup(s) in {AppPaths.BackupsDir} — newest {File.GetLastWriteTime(backups[0]):g}"
            : $"Backups are saved automatically to {AppPaths.BackupsDir} before every import or restore.", Lbl.Small, true));
        sp.Children.Add(Ui.Group("Import previous data", im));

        // ---- export folders ----
        var ex = new StackPanel();
        ex.Children.Add(Ui.Text("Save dialogs open in these folders, and remember the last folder you used. " +
                                "Empty = your Documents folder.", Lbl.Sub, true));
        void ExportRow(string label, Func<string> get, Action<string> set)
        {
            ex.Children.Add(Ui.M(Ui.Text(label, Lbl.FieldLbl), 0, 6, 0, 0));
            var v = get();
            ex.Children.Add(PathBox(string.IsNullOrEmpty(v) ? $"(Documents) {AppSettings.DocumentsDir}" : v));
            var browse = Ui.Button("Browse…", () => Run(() =>
            {
                var dlg = new OpenFolderDialog { Title = $"Default folder — {label}", InitialDirectory = AppSettings.ResolveDir(get()) };
                if (dlg.ShowDialog(this) != true) return false;
                set(dlg.FolderName);
                App.Settings.Save();
                return true;
            }), "GhostButton");
            var clear = Ui.Button("Use Documents", () => Run(() => { set(""); App.Settings.Save(); return true; }), "GhostButton");
            clear.IsEnabled = !string.IsNullOrEmpty(v);
            ex.Children.Add(Row(browse, clear, Ui.Button("Open folder", () => DataActions.OpenFolder(AppSettings.ResolveDir(get())), "GhostButton")));
        }
        ExportRow("Excel reports", () => App.Settings.ExportExcelDir, v => App.Settings.ExportExcelDir = v);
        ExportRow("Minutes — batch export (whole quarter)", () => App.Settings.ExportMinutesDir, v => App.Settings.ExportMinutesDir = v);
        ExportRow("Minutes — single meeting export", () => App.Settings.ExportSingleMinutesDir, v => App.Settings.ExportSingleMinutesDir = v);
        sp.Children.Add(Ui.Group("Default export folders", ex));

        // ---- install location ----
        var (installDir, kind) = UpdateService.InstallInfo();
        var ins = new StackPanel();
        ins.Children.Add(Ui.Text($"Version {UpdateService.CurrentVersion} — {kind}", Lbl.Sub, true));
        ins.Children.Add(PathBox(installDir));
        ins.Children.Add(Row(Ui.Button("Open folder", () => DataActions.OpenFolder(installDir), "GhostButton")));
        ins.Children.Add(Ui.Text("Program files only — your data is stored separately (above). To install somewhere else, run " +
                                 "Setup.exe --installto \"D:\\Apps\\FireDeptAttendance\" (use an empty folder), or extract the " +
                                 "Portable zip to any folder you can write to.", Lbl.Small, true));
        sp.Children.Add(Ui.Group("Install location", ins));

        _body.Content = sp;
    }
}
