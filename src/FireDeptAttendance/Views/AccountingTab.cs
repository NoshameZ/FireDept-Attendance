using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FireDeptAttendance.Core;
using FireDeptAttendance.Services;

namespace FireDeptAttendance.Views;

/// <summary>Per-quarter billing: counts, multiplier-weighted attendees, per-attendee rates and totals.</summary>
public sealed class AccountingTab : UserControl, IRefreshable
{
    private static readonly (string Key, string Label)[] Categories =
    {
        ("out_of_town", "Out of Town Calls"), ("in_town", "In Town Calls"), ("meetings", "Meetings"),
    };

    private readonly MainWindow _app;
    private Database Db => _app.Db;
    private readonly ComboBox _year = new() { Width = 72 };
    private readonly ComboBox _quarter = new() { Width = 130 };
    private readonly Dictionary<string, TextBox> _rates = new();
    private readonly Dictionary<string, TextBlock> _counts = new(), _atts = new(), _totals = new();
    private readonly Dictionary<string, long> _attendees = new();
    private readonly TextBlock _grand = Ui.Text("$0.00", Lbl.StatNum);
    private bool _building;

    public AccountingTab(MainWindow app)
    {
        _app = app;
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(Ui.Text("Accounting / Billing", Lbl.Header));
        sp.Children.Add(Ui.M(Ui.Text(
            "Per-attendee rates are applied to the (multiplier-weighted) number of attendees in each category. " +
            "Rates are saved automatically and shared across quarters.", Lbl.Sub, true), 0, 4, 0, 12));

        var sel = new StackPanel { Orientation = Orientation.Horizontal };
        sel.Children.Add(Ui.M(Ui.Text("Year:", Lbl.FieldLbl), 0, 0, 4, 0));
        for (var y = 2020; y <= DateTime.Today.Year + 2; y++) _year.Items.Add(y);
        sel.Children.Add(Ui.M(_year, 0, 0, 12, 0));
        sel.Children.Add(Ui.M(Ui.Text("Quarter:", Lbl.FieldLbl), 0, 0, 4, 0));
        for (var q = 1; q <= 4; q++) _quarter.Items.Add(DateUtil.QuarterLabel(q));
        sel.Children.Add(_quarter);
        sel.Children.Add(Ui.M(Ui.Button("Use current quarter", SyncToApp, "GhostButton"), 12, 0, 0, 0));
        sp.Children.Add(new GroupBox { Header = "Quarter to bill", Content = sel });
        _building = true;
        SelectQuarter(_app.CurYear, _app.CurQuarter);
        _building = false;
        _year.SelectionChanged += (_, _) => { if (!_building) Refresh(); };
        _quarter.SelectionChanged += (_, _) => { if (!_building) Refresh(); };

        var cards = new UniformGrid { Rows = 1, Columns = 3, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var (key, label) in Categories) cards.Children.Add(BuildCard(key, label));
        sp.Children.Add(cards);

        var gt = new DockPanel();
        var cap = Ui.Text("GRAND TOTAL", Lbl.StatCap);
        DockPanel.SetDock(cap, Dock.Left);
        gt.Children.Add(cap);
        _grand.HorizontalAlignment = HorizontalAlignment.Right;
        gt.Children.Add(_grand);
        var gtCard = Ui.Card(gt);
        gtCard.Padding = new Thickness(14);
        sp.Children.Add(Ui.M(gtCard, 0, 16, 0, 0));

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = sp };
    }

    private Border BuildCard(string key, string label)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text(label, Lbl.SideTitle));

        var info = new Grid { Margin = new Thickness(0, 8, 0, 6) };
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        info.ColumnDefinitions.Add(new ColumnDefinition());
        void InfoRow(int r, string caption, Dictionary<string, TextBlock> into)
        {
            info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var c = Ui.M(Ui.Text(caption, Lbl.StatCap), 0, r == 0 ? 0 : 2, 0, 0);
            var v = Ui.M(Ui.Text("0", Lbl.Value), 0, r == 0 ? 0 : 2, 0, 0);
            v.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(c, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
            info.Children.Add(c); info.Children.Add(v);
            into[key] = v;
        }
        InfoRow(0, "Number:", _counts);
        InfoRow(1, "Attendees:", _atts);
        sp.Children.Add(info);

        var rate = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
        rate.Children.Add(Ui.Text("Rate / attendee:  $", Lbl.StatCap));
        var tb = new TextBox { Width = 90 };
        tb.TextChanged += (_, _) => OnRateChange();
        rate.Children.Add(tb);
        _rates[key] = tb;
        sp.Children.Add(rate);

        sp.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 6) });

        var tot = new DockPanel();
        var tl = Ui.Text("Total:", Lbl.StatCap);
        DockPanel.SetDock(tl, Dock.Left);
        tot.Children.Add(tl);
        var tv = Ui.Text("$0.00", Lbl.StatNum);
        tv.HorizontalAlignment = HorizontalAlignment.Right;
        tot.Children.Add(tv);
        _totals[key] = tv;
        sp.Children.Add(tot);

        var card = Ui.Card(sp);
        card.Padding = new Thickness(12);
        return Ui.M(card, 6, 0, 6, 0);
    }

    private void SelectQuarter(int year, int q)
    {
        if (!_year.Items.Contains(year)) _year.Items.Add(year);
        _year.SelectedItem = year;
        _quarter.SelectedIndex = q - 1;
    }

    private void SyncToApp()
    {
        _building = true;
        SelectQuarter(_app.CurYear, _app.CurQuarter);
        _building = false;
        Refresh();
    }

    private Dictionary<string, double> CurrentRates()
    {
        var r = new Dictionary<string, double>();
        foreach (var (key, _) in Categories)
        {
            var raw = _rates[key].Text.Trim().Replace(",", "");
            r[key] = raw.Length > 0 && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                     && !double.IsNaN(v) ? Math.Max(0.0, v) : 0.0;
        }
        return r;
    }

    private void OnRateChange()
    {
        if (_building) return;
        var r = CurrentRates();
        var s = _app.Settings;
        s.RateOutOfTown = r["out_of_town"];
        s.RateInTown = r["in_town"];
        s.RateMeetings = r["meetings"];
        s.Save();
        Recalculate();
    }

    private void Recalculate()
    {
        var rates = CurrentRates();
        var grand = 0.0;
        foreach (var (key, _) in Categories)
        {
            var total = _attendees.GetValueOrDefault(key) * rates[key];
            grand += total;
            _totals[key].Text = Ui.Money(total);
        }
        _grand.Text = Ui.Money(grand);
    }

    public void Refresh()
    {
        var year = _year.SelectedItem is int y ? y : _app.CurYear;
        var q = _quarter.SelectedIndex >= 0 ? _quarter.SelectedIndex + 1 : 1;
        var stats = Db.AccountingStats(Db.GetOrCreateQuarter(year, q));
        var map = new Dictionary<string, CategoryCounts>
        {
            ["out_of_town"] = stats.OutOfTown, ["in_town"] = stats.InTown, ["meetings"] = stats.Meetings,
        };
        foreach (var (key, _) in Categories)
        {
            _attendees[key] = map[key].Attendees;
            _counts[key].Text = map[key].Count.ToString();
            _atts[key].Text = map[key].Attendees.ToString();
        }
        _building = true;
        var s = _app.Settings;
        var saved = new Dictionary<string, double>
        {
            ["out_of_town"] = s.RateOutOfTown, ["in_town"] = s.RateInTown, ["meetings"] = s.RateMeetings,
        };
        foreach (var (key, _) in Categories)
        {
            var v = saved[key];
            _rates[key].Text = v == 0 ? "" : v.ToString("0.00", CultureInfo.InvariantCulture);
        }
        _building = false;
        Recalculate();
    }
}
