using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FireDeptAttendance.Core;
using FireDeptAttendance.Services;

namespace FireDeptAttendance.Dialogs;

/// <summary>Settings / Preferences: theme, font size, accent colour (live), default locations, minutes templates.</summary>
public sealed class SettingsDialog : Window
{
    private readonly MainWindow _app;
    private AppSettings S => _app.Settings;
    private readonly RadioButton _light = new() { Content = "☀  Light", Margin = new Thickness(0, 0, 18, 0) };
    private readonly RadioButton _dark = new() { Content = "🌙  Dark" };
    private readonly List<RadioButton> _fonts = new();
    private readonly WrapPanel _swatches = new();
    private bool _syncing;
    private readonly TextBlock _dataPath = Ui.Text("", Lbl.Small);

    public SettingsDialog(MainWindow app)
    {
        _app = app;
        Ui.SetupDialog(this, "Settings / Preferences", app, 470, 720, resizable: false);
        SizeToContent = SizeToContent.Height;
        var (root, btns) = DialogLayout.Make();
        DialogLayout.Add(btns, Ui.Button("Reset to Defaults", Reset, "GhostButton"), Dock.Left);
        DialogLayout.Add(btns, Ui.Button("Close", Close, "AccentButton"), Dock.Right);

        var sp = new StackPanel();
        sp.Children.Add(Ui.M(Ui.Text("Settings / Preferences", Lbl.Header), 0, 0, 0, 2));
        sp.Children.Add(Ui.M(Ui.Text("Changes apply instantly and are saved automatically.", Lbl.Sub), 0, 0, 0, 6));

        var theme = new StackPanel { Orientation = Orientation.Horizontal };
        theme.Children.Add(_light);
        theme.Children.Add(_dark);
        _light.Checked += (_, _) => OnTheme("light");
        _dark.Checked += (_, _) => OnTheme("dark");
        sp.Children.Add(Ui.Group("Color Theme", theme));

        var font = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var size in AppSettings.FontSizes)
        {
            var rb = new RadioButton { Content = size, Tag = size, Margin = new Thickness(0, 0, 14, 0) };
            rb.Checked += (_, _) => OnFont(size);
            _fonts.Add(rb);
            font.Children.Add(rb);
        }
        sp.Children.Add(Ui.Group("Font Size", font));

        var acc = new StackPanel();
        acc.Children.Add(_swatches);
        var custom = Ui.Button("Custom…", PickCustom, "GhostButton");
        custom.HorizontalAlignment = HorizontalAlignment.Left;
        acc.Children.Add(Ui.M(custom, 0, 10, 0, 0));
        sp.Children.Add(Ui.Group("Accent Color", acc));

        var def = new StackPanel();
        def.Children.Add(Ui.M(Ui.Text("Set a default location for meetings and for specific call types. These are pre-filled " +
                                      "when you add a new call or meeting (and can still be changed each time).", Lbl.Sub, true), 0, 0, 0, 8));
        var defBtn = Ui.Button("🏢  Manage Default Locations…", () => new DefaultsDialog(this).ShowDialog(), "GhostButton");
        defBtn.HorizontalAlignment = HorizontalAlignment.Left;
        def.Children.Add(defBtn);
        sp.Children.Add(Ui.Group("Default Locations", def));

        var min = new StackPanel();
        min.Children.Add(Ui.M(Ui.Text("Edit the header and footer that wrap the minutes you type for each meeting. Use " +
                                      "{attendees} where the attendance count should appear — it fills in automatically.", Lbl.Sub, true), 0, 0, 0, 8));
        var minBtn = Ui.Button("📝  Edit Minutes Header / Footer…", () => new MinutesTemplateDialog(this).ShowDialog(), "GhostButton");
        minBtn.HorizontalAlignment = HorizontalAlignment.Left;
        min.Children.Add(minBtn);
        sp.Children.Add(Ui.Group("Meeting Minutes", min));

        var data = new StackPanel();
        data.Children.Add(Ui.M(Ui.Text("Data folder, importing a previous fire_dept.db, restoring backups, default export " +
                                       "folders and the install location.", Lbl.Sub, true), 0, 0, 0, 4));
        _dataPath.TextWrapping = TextWrapping.Wrap;
        data.Children.Add(Ui.M(_dataPath, 0, 0, 0, 8));
        var dataBtns = new WrapPanel();
        dataBtns.Children.Add(Ui.M(Ui.Button("💾  Data & Storage…", () =>
        {
            new DataDialog(this).ShowDialog();
            SyncControls();
        }, "GhostButton"), 0, 0, 8, 0));
        dataBtns.Children.Add(Ui.Button("📥  Import from database…", () =>
        {
            DataActions.ImportFromDatabase(this);
            SyncControls();
        }, "GhostButton"));
        data.Children.Add(dataBtns);
        sp.Children.Add(Ui.Group("Data & Storage", data));

        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = sp });
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        Content = root;
        SyncControls();
    }

    private void SyncControls()
    {
        _syncing = true;
        _light.IsChecked = S.Theme != "dark";
        _dark.IsChecked = S.Theme == "dark";
        foreach (var rb in _fonts) rb.IsChecked = (string)rb.Tag == S.FontSize;
        RenderSwatches();
        _dataPath.Text = $"Data folder: {AppPaths.DataDir}";
        _syncing = false;
    }

    private void RenderSwatches()
    {
        _swatches.Children.Clear();
        var current = S.Accent.ToLowerInvariant();
        foreach (var color in AppSettings.AccentPresets)
        {
            var sel = color.ToLowerInvariant() == current;
            var sw = new Border
            {
                Width = 34, Height = 30, Cursor = Cursors.Hand, Margin = new Thickness(4, 2, 4, 2),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                BorderThickness = new Thickness(sel ? 3 : 1),
                ToolTip = color,
            };
            sw.SetResourceReference(Border.BorderBrushProperty, sel ? "Text" : "Border");
            var c = color;
            sw.MouseLeftButtonUp += (_, _) => OnAccent(c);
            _swatches.Children.Add(sw);
        }
    }

    private void OnTheme(string theme)
    {
        if (_syncing || S.Theme == theme) return;
        S.Theme = theme;
        _app.ApplySettings();
        SyncControls();
    }

    private void OnFont(string size)
    {
        if (_syncing || S.FontSize == size) return;
        S.FontSize = size;
        _app.ApplySettings();
        SyncControls();
    }

    private void OnAccent(string color)
    {
        S.Accent = color;
        _app.ApplySettings();
        SyncControls();
    }

    private void PickCustom()
    {
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(AppSettings.IsHex(S.Accent) ? S.Accent : "#B22222");
            dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        }
        catch { /* keep default */ }
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        OnAccent($"#{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}");
    }

    private void Reset()
    {
        S.ResetToDefaults();
        _app.ApplySettings();
        SyncControls();
    }
}

/// <summary>Default meeting location + call type → location pairs that pre-fill the Add Call / Meeting form.</summary>
public sealed class DefaultsDialog : Window
{
    private readonly AppSettings _s = App.Settings;
    private readonly List<string> _known = App.Db.GetDistinctCallTypes();
    private readonly TextBox _meeting;
    private readonly StackPanel _rowsPanel = new();
    private readonly List<(ComboBox Type, TextBox Loc, FrameworkElement Row)> _rows = new();

    public DefaultsDialog(Window? owner)
    {
        Ui.SetupDialog(this, "Default Locations", owner, 560, 560);
        MinWidth = 520; MinHeight = 420;
        var (root, btns) = DialogLayout.Make();
        btns.Margin = new Thickness(0, 12, 0, 0);
        DialogLayout.Add(btns, Ui.Button("Save", Save, "AccentButton"), Dock.Right);
        DialogLayout.Add(btns, Ui.Button("Cancel", Close), Dock.Right);

        var top = new StackPanel();
        top.Children.Add(Ui.M(Ui.Text("Default Locations", Lbl.Header), 0, 0, 0, 2));
        top.Children.Add(Ui.M(Ui.Text("These locations are suggested automatically when you add a new call or meeting. You can " +
                                      "still change the location on any individual record.", Lbl.Sub, true), 0, 0, 0, 6));
        var mg = Ui.FormGrid();
        _meeting = new TextBox { Text = _s.DefaultMeetingLocation };
        Ui.AddRow(mg, 0, "Default meeting location:", _meeting);
        ((TextBlock)mg.Children[0]).FontWeight = FontWeights.Normal;
        top.Children.Add(Ui.Group("Meetings", mg));
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var call = new DockPanel();
        var intro = new StackPanel();
        intro.Children.Add(Ui.M(Ui.Text("Assign a default location to specific call types:", Lbl.Sub), 0, 0, 0, 6));
        var hdr = new Grid();
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition());
        var h1 = DialogLayout.Bold("Call type"); h1.Margin = new Thickness(2, 0, 6, 0);
        var h2 = DialogLayout.Bold("Default location"); Grid.SetColumn(h2, 1);
        hdr.Children.Add(h1); hdr.Children.Add(h2);
        intro.Children.Add(hdr);
        DockPanel.SetDock(intro, Dock.Top);
        call.Children.Add(intro);
        var add = Ui.Button("✚ Add call type", () => AddRow("", ""), "GhostButton");
        add.HorizontalAlignment = HorizontalAlignment.Left;
        DockPanel.SetDock(add, Dock.Bottom);
        call.Children.Add(add);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _rowsPanel, MinHeight = 180, Margin = new Thickness(0, 4, 0, 6) };
        var box = new Border { BorderThickness = new Thickness(1), Child = scroll };
        box.SetResourceReference(Border.BorderBrushProperty, "Border");
        Ui.Bg(box, "Card");
        call.Children.Add(box);
        var grp = Ui.Group("Call Types", call);
        grp.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(grp);
        Content = root;

        var seen = new HashSet<string>();
        foreach (var (ct, loc) in _s.CallLocations) { AddRow(ct, loc); seen.Add(ct); }
        foreach (var ct in _known)
            if (!string.IsNullOrEmpty(ct) && seen.Add(ct)) AddRow(ct, "");
        if (_rows.Count == 0) AddRow("", "");
    }

    private void AddRow(string ctype, string loc)
    {
        var g = new Grid { Margin = new Thickness(2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cb = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, Margin = new Thickness(0, 0, 6, 0) };
        foreach (var k in _known) cb.Items.Add(k);
        cb.Text = ctype;
        var tb = new TextBox { Text = loc, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(tb, 1);
        var entry = (cb, tb, (FrameworkElement)g);
        var rm = Ui.Button("✕", () => { _rowsPanel.Children.Remove(g); _rows.Remove(entry); }, "GhostButton");
        rm.Width = 30;
        Grid.SetColumn(rm, 2);
        g.Children.Add(cb); g.Children.Add(tb); g.Children.Add(rm);
        _rowsPanel.Children.Add(g);
        _rows.Add(entry);
    }

    private void Save()
    {
        var map = new Dictionary<string, string>();
        foreach (var (type, loc, _) in _rows)
        {
            var ct = (type.Text ?? "").Trim();
            var l = loc.Text.Trim();
            if (ct.Length > 0 && l.Length > 0) map[ct] = l;
        }
        _s.DefaultMeetingLocation = _meeting.Text.Trim();
        _s.CallLocations = map;
        _s.Save();
        Close();
    }
}
