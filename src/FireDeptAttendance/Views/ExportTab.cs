using System.Windows;
using System.Windows.Controls;
using FireDeptAttendance.Core;
using FireDeptAttendance.Services;
using Microsoft.Win32;

namespace FireDeptAttendance.Views;

/// <summary>Excel workbook export + batch Word minutes export. Exports run off the UI thread.</summary>
public sealed class ExportTab : UserControl, IRefreshable
{
    private readonly MainWindow _app;
    private Database Db => _app.Db;
    private readonly RadioButton _current = new() { Content = "Export current quarter only", IsChecked = true };
    private readonly RadioButton _all = new() { Content = "Export ALL quarters (one sheet each)" };
    private readonly CheckBox _activeOnly = new() { Content = "Include active members only", Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _roster = new() { Content = "Include Member Roster sheet", Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _status = Ui.Text("", Lbl.Sub, true);
    private readonly TextBlock _minStatus = Ui.Text("", Lbl.Sub, true);
    private readonly ComboBox _minYear = new() { Width = 72 };
    private readonly ComboBox _minQuarter = new() { Width = 130 };
    private readonly Button _xlsBtn, _minBtn;

    public ExportTab(MainWindow app)
    {
        _app = app;
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(Ui.Text("Excel Export", Lbl.Header));
        sp.Children.Add(Ui.M(Ui.Text(
            "Generate a formatted, multi-sheet Excel workbook. For each quarter you get a Summary sheet (members in rows, " +
            "months in columns — meetings out of 6, plus in-town / out-of-town calls weighted by their multiplier) and a " +
            "Call Details sheet with members down the side and each meeting/call across the top (showing its multiplier), " +
            "an X marking who attended. The Member Roster sheet is optional (off by default).", Lbl.Sub, true), 0, 4, 0, 12));

        var opt = new StackPanel();
        opt.Children.Add(_current);
        opt.Children.Add(_all);
        opt.Children.Add(_activeOnly);
        opt.Children.Add(_roster);
        sp.Children.Add(new GroupBox { Header = "Options", Content = opt });

        _xlsBtn = Ui.Button("📊 Generate Excel Report", ExportExcel, "AccentButton");
        _xlsBtn.HorizontalAlignment = HorizontalAlignment.Left;
        sp.Children.Add(Ui.M(_xlsBtn, 0, 14, 0, 14));
        sp.Children.Add(_status);

        sp.Children.Add(new Separator { Margin = new Thickness(0, 18, 0, 0) });
        sp.Children.Add(Ui.M(Ui.Text("Meeting Minutes (Word)", Lbl.Header), 0, 14, 0, 0));
        sp.Children.Add(Ui.M(Ui.Text(
            "Export every meeting's minutes for a quarter at once. Each meeting is saved as its own Word (.docx) document — " +
            "using the same formatting as the single-meeting minutes export — inside a new \"Minutes Q# YYYY\" folder, so " +
            "they can be printed or shared separately. Meetings with no minutes typed yet are still saved, with a \"No " +
            "minutes recorded\" note.", Lbl.Sub, true), 0, 4, 0, 10));

        var msel = new StackPanel { Orientation = Orientation.Horizontal };
        msel.Children.Add(Ui.M(Ui.Text("Year:", Lbl.FieldLbl), 0, 0, 4, 0));
        for (var y = 2020; y <= DateTime.Today.Year + 2; y++) _minYear.Items.Add(y);
        msel.Children.Add(Ui.M(_minYear, 0, 0, 12, 0));
        msel.Children.Add(Ui.M(Ui.Text("Quarter:", Lbl.FieldLbl), 0, 0, 4, 0));
        for (var q = 1; q <= 4; q++) _minQuarter.Items.Add(DateUtil.QuarterLabel(q));
        msel.Children.Add(_minQuarter);
        msel.Children.Add(Ui.M(Ui.Button("Use current quarter", SyncMinToApp, "GhostButton"), 12, 0, 0, 0));
        sp.Children.Add(new GroupBox { Header = "Quarter to export", Content = msel });
        SyncMinToApp();

        _minBtn = Ui.Button("📄 Export All Minutes for Quarter…", ExportMinutes, "AccentButton");
        _minBtn.HorizontalAlignment = HorizontalAlignment.Left;
        sp.Children.Add(Ui.M(_minBtn, 0, 14, 0, 14));
        sp.Children.Add(_minStatus);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = sp };
    }

    public void Refresh() { }

    private void SyncMinToApp()
    {
        if (!_minYear.Items.Contains(_app.CurYear)) _minYear.Items.Add(_app.CurYear);
        _minYear.SelectedItem = _app.CurYear;
        _minQuarter.SelectedIndex = _app.CurQuarter - 1;
    }

    private async void ExportMinutes()
    {
        var year = _minYear.SelectedItem is int y ? y : _app.CurYear;
        var q = _minQuarter.SelectedIndex >= 0 ? _minQuarter.SelectedIndex + 1 : 1;
        var qid = Db.GetOrCreateQuarter(year, q);
        var label = $"Q{q} {year}";
        if (!Db.GetEvents(qid).Any(e => e.IsMeeting))
        {
            Ui.Info("No meetings", $"There are no meetings in {label}, so there is nothing to export.");
            return;
        }
        var dlg = new OpenFolderDialog { Title = "Choose a folder to save the minutes documents" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var folder = dlg.FolderName;
        var header = _app.Settings.MinutesHeader;
        var footer = _app.Settings.MinutesFooter;
        var sig = _app.Settings.SignaturePath();

        QuarterMinutesSummary summary;
        _minBtn.IsEnabled = false;
        _minStatus.Text = "Exporting…";
        try
        {
            summary = await Task.Run(() => DocxExporter.ExportQuarterMinutes(
                Db, qid, folder, header, footer, string.IsNullOrEmpty(sig) ? null : sig));
        }
        catch (Exception e)
        {
            _minStatus.Text = "";
            Ui.Error("Export failed", e.Message);
            return;
        }
        finally { _minBtn.IsEnabled = true; }

        var count = summary.Exported;
        var blank = summary.Blank;
        var s = count != 1 ? "s" : "";
        var msg = $"Exported {count} minutes document{s} to:\n{summary.Folder}";
        if (blank > 0)
            msg += $"\n\n{blank} meeting{(blank != 1 ? "s" : "")} had no minutes recorded and " +
                   $"{(blank != 1 ? "were" : "was")} exported with a \"No minutes recorded\" note.";
        _minStatus.Text = $"✅ {count} minutes document{s} saved to:  {summary.Folder}";
        Ui.Info("Minutes exported", msg);
    }

    private async void ExportExcel()
    {
        List<long> ids;
        string name;
        if (_current.IsChecked == true)
        {
            ids = new() { _app.QuarterId };
            name = $"FireDept_{_app.CurrentQuarterText.Replace(' ', '_')}.xlsx";
        }
        else
        {
            ids = Db.ListQuarters().Select(q => q.Id).ToList();
            name = "FireDept_All_Quarters.xlsx";
        }
        var dlg = new SaveFileDialog
        {
            Title = "Save Excel Report", DefaultExt = ".xlsx", FileName = name,
            Filter = "Excel Workbook (*.xlsx)|*.xlsx", AddExtension = true,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var path = dlg.FileName;
        var activeOnly = _activeOnly.IsChecked == true;
        var roster = _roster.IsChecked == true;

        _xlsBtn.IsEnabled = false;
        _status.Text = "Generating…";
        try
        {
            await Task.Run(() => ExcelExporter.ExportQuarterWorkbook(Db, path, ids, activeOnly, roster));
        }
        catch (Exception e)
        {
            _status.Text = "";
            Ui.Error("Export failed", e.Message);
            return;
        }
        finally { _xlsBtn.IsEnabled = true; }
        _status.Text = $"✅ Report saved to:  {path}";
        Ui.Info("Export complete", $"Excel report saved:\n{path}");
    }
}
