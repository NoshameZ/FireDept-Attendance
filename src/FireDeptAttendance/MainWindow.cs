using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FireDeptAttendance.Core;
using FireDeptAttendance.Dialogs;
using FireDeptAttendance.Services;
using FireDeptAttendance.Views;

namespace FireDeptAttendance;

public interface IRefreshable { void Refresh(); }

/// <summary>Main window: accent toolbar (year/quarter selector, settings, theme), 4 tabs, status bar.</summary>
public sealed class MainWindow : Window
{
    public Database Db => App.Db;
    public AppSettings Settings => App.Settings;

    public int CurYear { get; private set; }
    public int CurQuarter { get; private set; }
    public long QuarterId { get; private set; }

    private readonly ComboBox _yearCb = new() { Width = 72 };
    private readonly ComboBox _quarterCb = new() { Width = 130 };
    private readonly Button _themeBtn;
    private readonly TabControl _tabs = new() { Margin = new Thickness(8) };
    private bool _syncing;
    private readonly TextBlock _verText;

    public MembersTab MembersTab { get; }
    public AttendanceTab AttendanceTab { get; }
    public ExportTab ExportTab { get; }
    public AccountingTab AccountingTab { get; }

    public string CurrentQuarterText => $"Q{CurQuarter} {CurYear}";

    public MainWindow()
    {
        Title = "Fire Department Attendance Tracker";
        Width = 1180; Height = 760; MinWidth = 940; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Ui.ApplyWindow(this);

        CurYear = DateTime.Today.Year;
        CurQuarter = DateUtil.CurrentQuarter();
        QuarterId = Db.GetOrCreateQuarter(CurYear, CurQuarter);

        var root = new DockPanel();

        // ---- toolbar ----
        var bar = new DockPanel { Height = 58, LastChildFill = false };
        Ui.Bg(bar, "Accent");
        var title = new TextBlock { Text = "🚒  Fire Dept Attendance", Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Ui.Font(title, "FontToolbarTitle", true);
        Ui.Fg(title, "OnAccent");
        DockPanel.SetDock(title, Dock.Left);
        bar.Children.Add(title);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(Ui.Fg(new TextBlock { Text = "Year:", Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center }, "OnAccent"));
        for (var y = 2020; y <= DateTime.Today.Year + 2; y++) _yearCb.Items.Add(y);
        right.Children.Add(Ui.M(_yearCb, 0, 0, 12, 0));
        right.Children.Add(Ui.Fg(new TextBlock { Text = "Quarter:", Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center }, "OnAccent"));
        for (var q = 1; q <= 4; q++) _quarterCb.Items.Add(DateUtil.QuarterLabel(q));
        right.Children.Add(_quarterCb);
        var sep = new Border { Width = 1, Height = 26, Margin = new Thickness(12, 0, 12, 0) };
        Ui.Bg(sep, "ToolSep");
        right.Children.Add(sep);
        right.Children.Add(Ui.Button("⚙  Settings", OpenSettings, "ToolButton"));
        _themeBtn = Ui.Button("", ToggleTheme, "ToolButton");
        right.Children.Add(Ui.M(_themeBtn, 8, 0, 0, 0));
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        // ---- status bar: version + manual update check ----
        var status = new DockPanel { Margin = new Thickness(10, 0, 10, 6) };
        var upd = Ui.Button("⟳  Check for Updates", () => _ = UpdateService.CheckAsync(this, manual: true), "GhostButton");
        upd.Padding = new Thickness(8, 2, 8, 2);
        DockPanel.SetDock(upd, Dock.Right);
        status.Children.Add(upd);
        var data = Ui.Button("💾  Data & Storage", () => new DataDialog(this).ShowDialog(), "GhostButton");
        data.Padding = new Thickness(8, 2, 8, 2);
        data.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(data, Dock.Right);
        status.Children.Add(data);
        _verText = Ui.Text("", Lbl.Sub);
        _verText.TextTrimming = TextTrimming.CharacterEllipsis;
        UpdateStatusText();
        status.Children.Add(_verText);
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        // ---- tabs ----
        MembersTab = new MembersTab(this);
        AttendanceTab = new AttendanceTab(this);
        ExportTab = new ExportTab(this);
        AccountingTab = new AccountingTab(this);
        _tabs.Items.Add(new TabItem { Header = "  Members  ", Content = MembersTab });
        _tabs.Items.Add(new TabItem { Header = "  Attendance  ", Content = AttendanceTab });
        _tabs.Items.Add(new TabItem { Header = "  Excel Export  ", Content = ExportTab });
        _tabs.Items.Add(new TabItem { Header = "  Accounting  ", Content = AccountingTab });
        _tabs.SelectionChanged += (s, e) =>
        {
            // Only refresh the tab that just became visible.
            if (e.OriginalSource == _tabs && _tabs.SelectedItem is TabItem { Content: IRefreshable r }) r.Refresh();
        };
        root.Children.Add(_tabs);
        Content = root;

        SyncToolbar();
        _yearCb.SelectionChanged += (_, _) => OnQuarterChange();
        _quarterCb.SelectionChanged += (_, _) => OnQuarterChange();
        UpdateThemeButton();

        Loaded += async (_, _) =>
        {
            RefreshAll();
            // Silent startup update check (no-op offline / when not installed).
            await Task.Delay(1500);
            await UpdateService.CheckAsync(this, manual: false);
        };
        Closed += (_, _) => { try { Db.Dispose(); } catch { /* ignore */ } };
    }

    private void SyncToolbar()
    {
        _syncing = true;
        if (!_yearCb.Items.Contains(CurYear))
        {
            var years = _yearCb.Items.Cast<int>().Append(CurYear).OrderBy(y => y).ToList();
            _yearCb.Items.Clear();
            foreach (var y in years) _yearCb.Items.Add(y);
        }
        _yearCb.SelectedItem = CurYear;
        _quarterCb.SelectedIndex = CurQuarter - 1;
        _syncing = false;
    }

    private void OnQuarterChange()
    {
        if (_syncing || _yearCb.SelectedItem is not int y || _quarterCb.SelectedIndex < 0) return;
        CurYear = y;
        CurQuarter = _quarterCb.SelectedIndex + 1;
        QuarterId = Db.GetOrCreateQuarter(CurYear, CurQuarter);
        RefreshAll();
    }

    /// <summary>Programmatically switch the active quarter (e.g. after auto-filing an event).</summary>
    public void SetQuarter(int year, int quarter)
    {
        CurYear = year;
        CurQuarter = quarter;
        QuarterId = Db.GetOrCreateQuarter(year, quarter);
        SyncToolbar();
    }

    private void UpdateStatusText()
    {
        _verText.Text = $"Version {UpdateService.CurrentVersion}   •   Data folder: {AppPaths.DataDir}";
        _verText.ToolTip = AppPaths.DbPath;
    }

    /// <summary>Called by <see cref="App.ReloadData"/> after the database/settings were swapped.</summary>
    public void OnDataReloaded()
    {
        QuarterId = Db.GetOrCreateQuarter(CurYear, CurQuarter);
        UpdateStatusText();
        UpdateThemeButton();
        RefreshAll();
    }

    public void RefreshAll()
    {
        MembersTab.Refresh();
        AttendanceTab.Refresh();
        ExportTab.Refresh();
        AccountingTab.Refresh();
    }

    // ---- settings / theme ----
    public void ApplySettings()
    {
        Settings.Save();
        ThemeManager.Apply(Settings);
        UpdateThemeButton();
        RefreshAll();
    }

    private void UpdateThemeButton() => _themeBtn.Content = Settings.Theme == "dark" ? "☀  Light" : "🌙  Dark";

    private void ToggleTheme()
    {
        Settings.Theme = Settings.Theme == "light" ? "dark" : "light";
        ApplySettings();
    }

    private void OpenSettings() => new SettingsDialog(this).ShowDialog();
}
