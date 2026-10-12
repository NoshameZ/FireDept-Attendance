using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FireDeptAttendance.Controls;
using FireDeptAttendance.Core;
using FireDeptAttendance.Dialogs;
using FireDeptAttendance.Services;

namespace FireDeptAttendance.Views;

/// <summary>Attendance tab: event list with Quick Totals on the left, read-only event detail / overview on the right.</summary>
public sealed class AttendanceTab : UserControl, IRefreshable
{
    public sealed class Row
    {
        public long Id { get; init; }
        public string Box { get; init; } = "";
        public string Qtr { get; init; } = "";
        public string Date { get; init; } = "";
        public string Type { get; init; } = "";
        public string Cat { get; init; } = "";
        public string Loc { get; init; } = "";
        public long Att { get; init; }
        public bool Included { get; init; }
        public bool IsCall { get; init; }
        // The multiplier is kept internally (no list column) for weighting.
        public int Mult { get; init; }
    }

    private static readonly Dictionary<string, string> Titles = new()
    {
        ["qtr"] = "Quarter", ["date"] = "Date", ["type"] = "Type",
        ["cat"] = "Category", ["loc"] = "Location", ["att"] = "Present",
    };

    private static readonly (string Key, string Label)[] TotalCats =
    {
        ("meetings", "Meetings"), ("in_town", "In-Town Calls"), ("out_of_town", "Out-of-Town Calls"),
    };

    private readonly MainWindow _app;
    private Database Db => _app.Db;
    private readonly TextBlock _headLbl = Ui.Text("Attendance", Lbl.Header);
    private readonly TextBlock _countLbl = Ui.Text("", Lbl.Sub);
    private readonly CheckBox _scopeAll = new() { Content = "All quarters", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _search = new();
    private readonly DataGrid _grid = new();
    private readonly StackPanel _detail = new() { Margin = new Thickness(12) };
    private readonly ScrollViewer _detailScroll;
    private readonly TextBlock _totScope = Ui.Text("", Lbl.Sub);
    private readonly Dictionary<string, TextBlock> _totLbls = new();
    private readonly TextBlock _totGrand = Ui.Text("0 attendees", Lbl.Value);
    private readonly Dictionary<long, bool> _included = new();   // view-only, never saved
    private string _sortCol = "date";
    private bool _sortRev;
    private long? _detailId;
    private bool _refreshing;

    public AttendanceTab(MainWindow app)
    {
        _app = app;
        var root = new DockPanel { Margin = new Thickness(10) };

        var header = new DockPanel();
        DockPanel.SetDock(_countLbl, Dock.Right);
        header.Children.Add(_countLbl);
        header.Children.Add(_headLbl);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var body = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(11, GridUnitType.Star) });

        // ---- left ----
        var left = new DockPanel();
        var ctrl = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = false };
        var add = Ui.Button("➕ Add New Call / Meeting", AddEvent, "AccentButton");
        DockPanel.SetDock(add, Dock.Left);
        ctrl.Children.Add(add);
        DockPanel.SetDock(_scopeAll, Dock.Right);
        ctrl.Children.Add(_scopeAll);
        _scopeAll.Click += (_, _) => Refresh();
        DockPanel.SetDock(ctrl, Dock.Top);
        left.Children.Add(ctrl);

        var sbar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var icon = Ui.Text("🔍");
        DockPanel.SetDock(icon, Dock.Left);
        sbar.Children.Add(icon);
        var clear = Ui.Button("Clear", () => _search.Text = "");
        DockPanel.SetDock(clear, Dock.Right);
        sbar.Children.Add(clear);
        sbar.Children.Add(Ui.M(_search, 4, 0, 6, 0));
        _search.TextChanged += (_, _) => Refresh();
        DockPanel.SetDock(sbar, Dock.Top);
        left.Children.Add(sbar);

        var totals = BuildTotalsPanel();
        DockPanel.SetDock(totals, Dock.Bottom);
        left.Children.Add(totals);

        _grid.Columns.Add(GridHelper.Col("✔", nameof(Row.Box), 34, null));
        _grid.Columns[0].CanUserResize = false;
        _grid.Columns.Add(GridHelper.Col("Quarter", nameof(Row.Qtr), 70, "qtr"));
        _grid.Columns.Add(GridHelper.Col("Date", nameof(Row.Date), 88, "date"));
        _grid.Columns.Add(GridHelper.Col("Type", nameof(Row.Type), 64, "type"));
        _grid.Columns.Add(GridHelper.Col("Category", nameof(Row.Cat), 84, "cat"));
        _grid.Columns.Add(GridHelper.Col("Location", nameof(Row.Loc), 140, "loc", center: false, star: true));
        _grid.Columns.Add(GridHelper.Col("Present", nameof(Row.Att), 62, "att"));
        GridHelper.HookSorting(_grid, () => (_sortCol, _sortRev), (c, r) => { _sortCol = c; _sortRev = r; Refresh(); });
        _grid.LoadingRow += (_, e) =>
        {
            if (e.Row.Item is not Row r) return;
            var key = !r.Included ? "Muted" : r.IsCall ? "AccentDark" : "Text";
            e.Row.SetResourceReference(Control.ForegroundProperty, key);
        };
        _grid.SelectionChanged += (_, _) => { if (!_refreshing) OnSelect(); };
        _grid.PreviewMouseLeftButtonDown += OnGridMouseDown;
        _grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && _grid.SelectedItem is Row r)
            {
                e.Handled = true;
                ToggleIncluded(r.Id);
            }
        };
        left.Children.Add(_grid);
        Grid.SetColumn(left, 0);
        body.Children.Add(left);

        var sep = new Border { Width = 1, Margin = new Thickness(8, 0, 8, 0) };
        Ui.Bg(sep, "Border");
        Grid.SetColumn(sep, 1);
        body.Children.Add(sep);

        _detailScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _detail };
        var side = new Border { BorderThickness = new Thickness(1), Child = _detailScroll };
        side.SetResourceReference(Border.BorderBrushProperty, "Border");
        Ui.Bg(side, "Panel");
        Grid.SetColumn(side, 2);
        body.Children.Add(side);

        root.Children.Add(body);
        Content = root;
        ShowOverview();
    }

    private bool ScopeAll => _scopeAll.IsChecked == true;
    private string ScopeText => ScopeAll ? "All quarters" : _app.CurrentQuarterText;

    // ------------------------------------------------------------------ Quick Totals
    private GroupBox BuildTotalsPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(_totScope);
        var row = new UniformGrid { Rows = 1, Columns = TotalCats.Length, Margin = new Thickness(0, 4, 0, 2) };
        foreach (var (key, label) in TotalCats)
        {
            var cell = new StackPanel { Margin = new Thickness(3, 0, 3, 0) };
            cell.Children.Add(Ui.Text(label, Lbl.StatCap));
            var val = Ui.Text("0 attendees (0)", Lbl.Value);
            cell.Children.Add(val);
            _totLbls[key] = val;
            row.Children.Add(cell);
        }
        panel.Children.Add(row);

        var gt = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(Ui.M(Ui.Button("Select All", () => SetAllIncluded(true), "GhostButton"), 2, 0, 2, 0));
        btns.Children.Add(Ui.M(Ui.Button("Deselect All", () => SetAllIncluded(false), "GhostButton"), 2, 0, 2, 0));
        DockPanel.SetDock(btns, Dock.Right);
        gt.Children.Add(btns);
        var gl = Ui.Text("Grand total:", Lbl.FieldLbl);
        DockPanel.SetDock(gl, Dock.Left);
        gt.Children.Add(gl);
        gt.Children.Add(Ui.M(_totGrand, 6, 0, 0, 0));
        panel.Children.Add(gt);

        panel.Children.Add(Ui.M(Ui.Text(
            "Attendees = present marks summed across included events; calls are multiplier-weighted " +
            "(a 2× call with 5 present = 10), meetings count once. Tick ✔ / untick ☐ an event in the list " +
            "to include or exclude it. Totals cover the whole selected quarter (search does not change them).",
            Lbl.Sub, true), 0, 2, 0, 0));
        return new GroupBox { Header = "Quick Totals", Content = panel, Margin = new Thickness(0, 8, 0, 0) };
    }

    private bool IsIncluded(long id) => !_included.TryGetValue(id, out var v) || v;

    private static string? TotalsCategory(Event ev) => ev.EventType switch
    {
        "meeting" => "meetings",
        "call" when ev.CallCategory == "in_town" => "in_town",
        "call" when ev.CallCategory == "out_of_town" => "out_of_town",
        _ => null,
    };

    private void RecomputeTotals(List<Event> all, Dictionary<long, long> counts)
    {
        var stats = TotalCats.ToDictionary(c => c.Key, _ => (Att: 0L, Events: 0));
        foreach (var ev in all)
        {
            if (!IsIncluded(ev.Id)) continue;
            var cat = TotalsCategory(ev);
            if (cat == null) continue;
            var present = counts.GetValueOrDefault(ev.Id);
            var mult = ev.IsCall ? ev.CallMultiplier : 1;
            var s = stats[cat];
            stats[cat] = (s.Att + present * mult, s.Events + 1);
        }
        long grand = 0;
        foreach (var (key, _) in TotalCats)
        {
            var (att, n) = stats[key];
            grand += att;
            _totLbls[key].Text = $"{att} attendees ({n} {(n == 1 ? "event" : "events")})";
        }
        _totGrand.Text = $"{grand} attendees";
        _totScope.Text = $"For {ScopeText}";
    }

    private void ToggleIncluded(long id)
    {
        _included[id] = !IsIncluded(id);
        Refresh();
    }

    private void SetAllIncluded(bool value)
    {
        foreach (var ev in ScopeEvents()) _included[ev.Id] = value;
        Refresh();
    }

    private List<Event> ScopeEvents() => ScopeAll ? Db.GetAllEvents() : Db.GetEvents(_app.QuarterId);

    private void OnGridMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Clicking the ✔ cell toggles inclusion without changing the selection.
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridCell && dep is not DataGridColumnHeader)
            dep = dep is Visual || dep is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(dep) : LogicalTreeHelper.GetParent(dep);
        if (dep is DataGridCell cell && cell.Column?.DisplayIndex == 0 && cell.DataContext is Row r)
        {
            e.Handled = true;
            ToggleIncluded(r.Id);
        }
    }

    // ------------------------------------------------------------------ list
    private static string CategoryLabel(Event ev) => ev.CallCategory switch
    {
        "in_town" => "In Town",
        "out_of_town" => "Out of Town",
        null or "" => "—",
        var c => c,
    };

    private static bool Matches(Event ev, string needle)
    {
        var parts = new[]
        {
            DateUtil.IsoToUs(ev.EventDate), ev.EventDate, ev.EventType, CategoryLabel(ev),
            ev.CallCategory ?? "", ev.Location, ev.Title,
        };
        return string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p))).ToLowerInvariant().Contains(needle);
    }

    private static string Title(string s) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase((s ?? "").ToLowerInvariant());

    public void Refresh()
    {
        _headLbl.Text = $"Attendance — {ScopeText}";
        var keep = _detailId;
        var all = ScopeEvents();
        var counts = Db.AttendeeCountsByEvent();
        RecomputeTotals(all, counts);

        var needle = _search.Text.Trim().ToLowerInvariant();
        var events = needle.Length > 0 ? all.Where(ev => Matches(ev, needle)).ToList() : all;

        IComparer<object> cmp = Comparer<object>.Create(CompareKeys);
        Func<Event, object> key = _sortCol switch
        {
            "qtr" => ev => (ev.Year, ev.QuarterNumber, ev.EventDate),
            "type" => ev => ev.EventType.ToLowerInvariant(),
            "cat" => ev => (ev.CallCategory ?? "").ToLowerInvariant(),
            "loc" => ev => ev.Location.ToLowerInvariant(),
            "att" => ev => counts.GetValueOrDefault(ev.Id),
            _ => ev => ev.EventDate,
        };
        var sorted = _sortRev ? events.OrderByDescending(key, cmp).ToList() : events.OrderBy(key, cmp).ToList();

        int meetings = 0, calls = 0;
        var rows = new List<Row>();
        foreach (var ev in sorted)
        {
            var inc = IsIncluded(ev.Id);
            if (ev.IsCall) calls++; else meetings++;
            rows.Add(new Row
            {
                Id = ev.Id,
                Box = inc ? "✔" : "☐",
                Qtr = ev.Year > 0 ? $"Q{ev.QuarterNumber} {ev.Year}" : _app.CurrentQuarterText,
                Date = DateUtil.IsoToUs(ev.EventDate),
                Type = Title(ev.EventType),
                Cat = CategoryLabel(ev),
                Loc = ev.Location,
                Att = counts.GetValueOrDefault(ev.Id),
                Included = inc,
                IsCall = ev.IsCall,
                Mult = ev.CallMultiplier,
            });
        }

        _refreshing = true;
        try
        {
            _grid.ItemsSource = rows;
            GridHelper.UpdateArrows(_grid, Titles, _sortCol, _sortRev);
            var held = Db.CountMeetings(_app.QuarterId);
            _countLbl.Text = $"Meetings: {meetings}  •  Calls: {calls}  •  This quarter's meetings: {held}/{Database.MeetingsPerQuarter}";
            var keepRow = keep is null ? null : rows.FirstOrDefault(r => r.Id == keep);
            if (keepRow != null) _grid.SelectedItem = keepRow;
        }
        finally { _refreshing = false; }

        if (keep is long k && rows.Any(r => r.Id == k))
        {
            var ev = Db.GetEvent(k);
            if (ev != null) ShowEvent(ev); else ShowOverview();
        }
        else ShowOverview();
    }

    private static int CompareKeys(object? a, object? b)
    {
        return (a, b) switch
        {
            (string x, string y) => string.CompareOrdinal(x, y),
            (long x, long y) => x.CompareTo(y),
            (ValueTuple<int, int, string> x, ValueTuple<int, int, string> y) =>
                x.Item1 != y.Item1 ? x.Item1.CompareTo(y.Item1)
                : x.Item2 != y.Item2 ? x.Item2.CompareTo(y.Item2)
                : string.CompareOrdinal(x.Item3, y.Item3),
            _ => 0,
        };
    }

    private void OnSelect()
    {
        if (_grid.SelectedItem is not Row r) { ShowOverview(); return; }
        var ev = Db.GetEvent(r.Id);
        if (ev == null) ShowOverview(); else ShowEvent(ev);
    }

    private void SelectEvent(long id)
    {
        if (_grid.ItemsSource is not List<Row> rows) return;
        var row = rows.FirstOrDefault(r => r.Id == id);
        if (row == null) { ShowOverview(); return; }
        _refreshing = true;
        _grid.SelectedItem = row;
        _refreshing = false;
        GridHelper.ScrollTo(_grid, row);
        var ev = Db.GetEvent(id);
        if (ev != null) ShowEvent(ev); else ShowOverview();
    }

    // ------------------------------------------------------------------ sidebar: overview
    private void ShowOverview()
    {
        _detailId = null;
        _detail.Children.Clear();
        var d = _detail;
        d.Children.Add(Ui.Text("Events Overview", Lbl.SideTitle));
        d.Children.Add(Ui.M(Ui.Text($"{ScopeText}.  Select a call or meeting on the left to edit it and mark attendance.", Lbl.Sub, true), 0, 2, 0, 12));
        var ov = Db.EventsOverview(ScopeAll ? null : _app.QuarterId);
        d.Children.Add(Ui.StatRow(
            (ov.TotalMeetings.ToString(), "Meetings"),
            (ov.TotalCalls.ToString(), "Calls"),
            (ov.TotalCallCredits.ToString(), "Call credits")));
        d.Children.Add(Ui.HSep());
        d.Children.Add(Ui.M(Ui.Text("Breakdown by month", Lbl.FieldLbl), 0, 0, 0, 6));

        var table = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        for (var c = 0; c < 4; c++) table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Cell(string text, int r, int c, bool bold)
        {
            while (table.RowDefinitions.Count <= r) table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var t = Ui.M(Ui.Text(text, Lbl.Small), 0, 2, 14, 2);
            if (bold) t.FontWeight = FontWeights.Bold;
            Grid.SetRow(t, r); Grid.SetColumn(t, c);
            table.Children.Add(t);
        }
        var heads = new[] { "Month", "Meetings", "Calls", "Credits" };
        for (var c = 0; c < heads.Length; c++) Cell(heads[c], 0, c, true);
        if (ov.ByMonth.Count > 0)
        {
            var r = 1;
            foreach (var (mo, slot) in ov.ByMonth)
            {
                var name = mo is >= 1 and <= 12 ? Database.MonthNames[mo] : mo.ToString();
                Cell(name, r, 0, false);
                Cell(slot.Meetings.ToString(), r, 1, false);
                Cell(slot.Calls.ToString(), r, 2, false);
                Cell(slot.CallCredits.ToString(), r, 3, false);
                r++;
            }
        }
        else
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var t = Ui.M(Ui.Fg(Ui.Text("No events recorded yet.", Lbl.Small), "Muted"), 0, 4, 0, 4);
            Grid.SetRow(t, 1); Grid.SetColumnSpan(t, 4);
            table.Children.Add(t);
        }
        d.Children.Add(table);
        d.Children.Add(Ui.M(Ui.Text("“Credits” weight each call by its multiplier (a x2 call counts as 2).", Lbl.Sub, true), 0, 12, 0, 0));
        _detailScroll.ScrollToTop();
    }

    // ------------------------------------------------------------------ sidebar: event detail (read-only)
    private void ShowEvent(Event ev)
    {
        var sameEvent = _detailId == ev.Id;
        _detailId = ev.Id;
        _detail.Children.Clear();
        var d = _detail;
        var isCall = ev.IsCall;

        var head = new DockPanel { LastChildFill = false };
        var title = Ui.Text($"{Title(ev.EventType)} — {DateUtil.IsoToUs(ev.EventDate)}", Lbl.SideTitle);
        DockPanel.SetDock(title, Dock.Left);
        head.Children.Add(title);
        var edit = Ui.Button("✎ Edit", EditEvent, "AccentButton");
        DockPanel.SetDock(edit, Dock.Right);
        head.Children.Add(edit);
        if (ev.IsMeeting)
        {
            var min = Ui.M(Ui.Button("📝 Minutes", () => OpenMinutes(ev), "GhostButton"), 0, 0, 6, 0);
            DockPanel.SetDock(min, Dock.Right);
            head.Children.Add(min);
        }
        d.Children.Add(head);

        var rows = new List<(string, string)> { ("Type", Title(ev.EventType)) };
        if (isCall)
        {
            var cat = Title((ev.CallCategory ?? "").Replace('_', ' '));
            rows.Add(("Category", cat.Length > 0 ? cat : "—"));
        }
        rows.Add(("Date", DateUtil.IsoToUs(ev.EventDate)));
        rows.Add(("Location", ev.Location.Length > 0 ? ev.Location : "—"));
        rows.Add(("Call Type", ev.Title.Length > 0 ? ev.Title : "—"));
        if (isCall) rows.Add(("Multiplier", $"x{ev.CallMultiplier}"));
        var det = Ui.FormGrid();
        for (var i = 0; i < rows.Count; i++)
        {
            var v = Ui.Text(rows[i].Item2, Lbl.Value, true);
            Ui.AddRow(det, i, rows[i].Item1, v);
        }
        foreach (FrameworkElement fe in det.Children) fe.Margin = new Thickness(fe.Margin.Left, 2, fe.Margin.Right, 2);
        d.Children.Add(Ui.Group("Details", det));

        var existing = Db.GetEventAttendance(ev.Id);
        var members = Db.GetMembersForEvent(ev.EventDate);
        var present = members.Count(m => existing.GetValueOrDefault(m.Id));
        FrameworkElement list;
        if (members.Count == 0)
        {
            list = Ui.M(Ui.Fg(Ui.Text("No eligible members for this date.", Lbl.Small), "Muted"), 6, 6, 6, 6);
        }
        else
        {
            var ug = new UniformGrid { Columns = 2 };
            foreach (var m in members)
            {
                var isPresent = existing.GetValueOrDefault(m.Id);
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 8, 2) };
                var mark = new TextBlock { Text = isPresent ? "✓" : "•", Width = 18, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
                Ui.Font(mark, "FontBase", true);
                Ui.Fg(mark, isPresent ? "Accent" : "Muted");
                cell.Children.Add(mark);
                var name = Ui.Text(m.Active ? m.Name : $"{m.Name} (now inactive)", Lbl.Small);
                Ui.Fg(name, isPresent ? "Text" : "Muted");
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                cell.Children.Add(name);
                ug.Children.Add(cell);
            }
            list = ug;
        }
        var listBox = new Border { BorderThickness = new Thickness(1), Child = list, Padding = new Thickness(2, 4, 2, 4) };
        listBox.SetResourceReference(Border.BorderBrushProperty, "Border");
        Ui.Bg(listBox, "Card");
        d.Children.Add(Ui.M(Ui.Group($"Attendance — {present} of {members.Count} present", listBox), 0, 10, 0, 0));
        d.Children.Add(Ui.M(Ui.Text("Use ✎ Edit to change details or mark attendance.", Lbl.Sub, true), 0, 8, 0, 0));
        if (!sameEvent) _detailScroll.ScrollToTop();
    }

    // ------------------------------------------------------------------ add / edit / minutes
    private void FollowQuarter(Event? ev, bool onlyIfChanged)
    {
        if (ev == null || ScopeAll) return;
        var q = Db.GetQuarter(ev.QuarterId);
        if (q == null) return;
        if (!onlyIfChanged || q.Year != _app.CurYear || q.Number != _app.CurQuarter) _app.SetQuarter(q.Year, q.Number);
    }

    private void RefreshOthers()
    {
        _app.MembersTab.Refresh();
        _app.AccountingTab.Refresh();
    }

    private void AddEvent()
    {
        var dlg = new EventDialog(Window.GetWindow(this), null);
        dlg.ShowDialog();
        if (dlg.SavedId is not long id) return;
        // The event was auto-filed by date; jump to its quarter so it's visible.
        FollowQuarter(Db.GetEvent(id), onlyIfChanged: false);
        _detailId = id;
        Refresh();
        SelectEvent(id);
        RefreshOthers();
    }

    private void EditEvent()
    {
        if (_detailId is not long eid) return;
        var ev = Db.GetEvent(eid);
        if (ev == null) return;
        var dlg = new EventDialog(Window.GetWindow(this), ev);
        dlg.ShowDialog();
        if (dlg.Deleted)
        {
            _detailId = null;
            Refresh();
            RefreshOthers();
            ShowOverview();
            return;
        }
        if (dlg.SavedId is null) return;
        FollowQuarter(Db.GetEvent(eid), onlyIfChanged: true);
        Refresh();
        RefreshOthers();
        if (_grid.ItemsSource is List<Row> rows && rows.Any(r => r.Id == eid)) SelectEvent(eid);
        else ShowOverview();
    }

    private void OpenMinutes(Event ev) => new MinutesDialog(Window.GetWindow(this), ev).ShowDialog();
}
