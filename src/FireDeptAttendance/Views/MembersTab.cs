using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FireDeptAttendance.Controls;
using FireDeptAttendance.Core;
using FireDeptAttendance.Dialogs;
using FireDeptAttendance.Services;

namespace FireDeptAttendance.Views;

/// <summary>Members tab: searchable, sortable roster on the left; overview / editor sidebar on the right.</summary>
public sealed class MembersTab : UserControl, IRefreshable
{
    public sealed class Row
    {
        public long Id { get; init; }
        public string Last { get; init; } = "";
        public string First { get; init; } = "";
        public string Mi { get; init; } = "";
        public string Nick { get; init; } = "";
        public string Radio { get; init; } = "";
        public string Status { get; init; } = "";
        public bool Active { get; init; }
    }

    private static readonly Dictionary<string, string> Titles = new()
    {
        ["last"] = "Last Name", ["first"] = "First Name", ["mi"] = "MI",
        ["nick"] = "Nickname", ["radio"] = "Radio #", ["status"] = "Status",
    };

    private readonly MainWindow _app;
    private Database Db => _app.Db;
    private readonly TextBlock _countLbl = Ui.Text("", Lbl.Sub);
    private readonly TextBox _search = new();
    private readonly DataGrid _grid = new();
    private readonly StackPanel _detail = new() { Margin = new Thickness(12) };
    private readonly ScrollViewer _detailScroll;
    private string _sortCol = "last";
    private bool _sortRev;
    private long? _detailId;
    private bool _refreshing;

    // editor fields (rebuilt per selection)
    private TextBox _first = null!, _mi = null!, _last = null!, _nick = null!, _radio = null!;
    private ComboBox _status = null!;
    private DateBox _join = null!, _inactive = null!;

    public MembersTab(MainWindow app)
    {
        _app = app;
        var root = new DockPanel { Margin = new Thickness(10) };

        var header = new DockPanel();
        var h = Ui.Text("Member Management", Lbl.Header);
        DockPanel.SetDock(_countLbl, Dock.Right);
        header.Children.Add(_countLbl);
        header.Children.Add(h);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var body = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(11, GridUnitType.Star) });

        // ---- left: search + add + roster ----
        var left = new DockPanel();
        var ctrl = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var add = Ui.Button("➕ Add New Member", AddMember, "AccentButton");
        DockPanel.SetDock(add, Dock.Right);
        ctrl.Children.Add(add);
        var icon = Ui.Text("🔍");
        DockPanel.SetDock(icon, Dock.Left);
        ctrl.Children.Add(icon);
        ctrl.Children.Add(Ui.M(_search, 4, 0, 8, 0));
        _search.TextChanged += (_, _) => Refresh();
        DockPanel.SetDock(ctrl, Dock.Top);
        left.Children.Add(ctrl);

        _grid.Columns.Add(GridHelper.Col("Last Name", nameof(Row.Last), 110, "last", center: false, star: true));
        _grid.Columns.Add(GridHelper.Col("First Name", nameof(Row.First), 100, "first", center: false, star: true));
        _grid.Columns.Add(GridHelper.Col("MI", nameof(Row.Mi), 36, "mi"));
        _grid.Columns.Add(GridHelper.Col("Nickname", nameof(Row.Nick), 90, "nick", center: false, star: true));
        _grid.Columns.Add(GridHelper.Col("Radio #", nameof(Row.Radio), 64, "radio"));
        _grid.Columns.Add(GridHelper.Col("Status", nameof(Row.Status), 70, "status"));
        GridHelper.HookSorting(_grid, () => (_sortCol, _sortRev), (c, r) => { _sortCol = c; _sortRev = r; Refresh(); });
        _grid.LoadingRow += (_, e) =>
        {
            if (e.Row.Item is Row r)
            {
                if (r.Active) e.Row.SetResourceReference(Control.ForegroundProperty, "Text");
                else e.Row.SetResourceReference(Control.ForegroundProperty, "Inactive");
            }
        };
        _grid.SelectionChanged += (_, _) => { if (!_refreshing) OnSelect(); };
        left.Children.Add(_grid);
        Grid.SetColumn(left, 0);
        body.Children.Add(left);

        var sep = new Border { Width = 1, Margin = new Thickness(8, 0, 8, 0) };
        Ui.Bg(sep, "Border");
        Grid.SetColumn(sep, 1);
        body.Children.Add(sep);

        // ---- right: scrollable sidebar ----
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

    // ------------------------------------------------------------------ list
    public void Refresh()
    {
        var keep = _detailId;
        _refreshing = true;
        try
        {
            var search = _search.Text.Trim();
            var members = Db.GetMembers(search: search.Length > 0 ? search : null);
            Func<Member, string> key = _sortCol switch
            {
                "first" => m => m.FirstName.ToLowerInvariant(),
                "mi" => m => m.MiddleInitial.ToLowerInvariant(),
                "nick" => m => m.Nickname.ToLowerInvariant(),
                "radio" => m => m.RadioNumber.ToLowerInvariant(),
                "status" => m => m.Active ? "active" : "inactive",
                _ => m => m.LastName.ToLowerInvariant(),
            };
            // Stable sort (like Python's sorted) in either direction.
            var sorted = _sortRev
                ? members.OrderByDescending(key, StringComparer.Ordinal).ToList()
                : members.OrderBy(key, StringComparer.Ordinal).ToList();
            var rows = sorted.Select(m => new Row
            {
                Id = m.Id, Last = m.LastName, First = m.FirstName, Mi = m.MiddleInitial, Nick = m.Nickname,
                Radio = m.RadioNumber, Status = m.Active ? "Active" : "Inactive", Active = m.Active,
            }).ToList();
            _grid.ItemsSource = rows;
            GridHelper.UpdateArrows(_grid, Titles, _sortCol, _sortRev);

            var total = Db.CountMembers();
            var active = Db.CountActiveMembers();
            _countLbl.Text = $"Active: {active}/{Database.MaxMembers}  •  Inactive: {total - active}  •  Total roster: {total}";

            var keepRow = keep is null ? null : rows.FirstOrDefault(r => r.Id == keep);
            if (keepRow != null) _grid.SelectedItem = keepRow;
        }
        finally { _refreshing = false; }

        if (keep is not null)
        {
            // Re-render the sidebar from fresh data (or fall back to the overview if gone).
            var m = Db.GetMember(keep.Value);
            if (m != null && _grid.SelectedItem is Row) ShowMember(m);
            else ShowOverview();
        }
        else ShowOverview();
    }

    private void OnSelect()
    {
        if (_grid.SelectedItem is not Row r) { ShowOverview(); return; }
        var m = Db.GetMember(r.Id);
        if (m == null) ShowOverview(); else ShowMember(m);
    }

    private void Select(long id)
    {
        if (_grid.ItemsSource is not List<Row> rows) return;
        var row = rows.FirstOrDefault(r => r.Id == id);
        if (row == null) { ShowOverview(); return; }
        _refreshing = true;
        _grid.SelectedItem = row;
        _refreshing = false;
        GridHelper.ScrollTo(_grid, row);
        var m = Db.GetMember(id);
        if (m != null) ShowMember(m);
    }

    // ------------------------------------------------------------------ sidebar: overview
    private void ShowOverview()
    {
        _detailId = null;
        _detail.Children.Clear();
        var d = _detail;
        d.Children.Add(Ui.Text("Department Overview", Lbl.SideTitle));
        d.Children.Add(Ui.M(Ui.Text("Select a member on the left to view and edit their details.", Lbl.Sub, true), 0, 2, 0, 12));
        var ds = Db.DepartmentStats();
        d.Children.Add(Ui.StatRow(
            ($"{ds.ActiveMembers}/{Database.MaxMembers}", "Active members"),
            (ds.InactiveMembers.ToString(), "Inactive members"),
            (ds.TotalMembers.ToString(), "Total roster")));
        d.Children.Add(Ui.HSep());
        d.Children.Add(Ui.M(Ui.Text("Department-wide attendance (all time)", Lbl.FieldLbl), 0, 0, 0, 6));
        var info = new Grid();
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        info.ColumnDefinitions.Add(new ColumnDefinition());
        var rows = new[]
        {
            ("Meetings recorded", ds.TotalMeetings), ("Calls recorded", ds.TotalCalls),
            ("Weighted call credits", ds.TotalCallCredits), ("Total attendance marks", ds.AttendanceMarks),
        };
        for (var i = 0; i < rows.Length; i++)
        {
            info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Ui.M(Ui.Text(rows[i].Item1 + ":", Lbl.FieldLbl), 0, 3, 0, 3);
            var v = Ui.M(Ui.Text(rows[i].Item2.ToString(), Lbl.Small), 16, 3, 0, 3);
            Grid.SetRow(l, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            info.Children.Add(l); info.Children.Add(v);
        }
        d.Children.Add(info);
        d.Children.Add(Ui.M(Ui.Text("“Weighted call credits” count each call by its multiplier — a x2 call counts as 2.", Lbl.Sub, true), 0, 12, 0, 0));
        _detailScroll.ScrollToTop();
    }

    // ------------------------------------------------------------------ sidebar: member editor
    private void ShowMember(Member m)
    {
        _detailId = m.Id;
        _detail.Children.Clear();
        var d = _detail;
        var isActive = m.Active;
        d.Children.Add(Ui.Text(m.Name, Lbl.SideTitle));
        d.Children.Add(Ui.M(Ui.Text("Edit this member's details below, then Save Changes.", Lbl.Sub, true), 0, 2, 0, 8));

        // --- Name ---
        var ng = Ui.FormGrid(2);
        _first = new TextBox { Text = m.FirstName };
        _mi = new TextBox { Text = m.MiddleInitial, Width = 50, HorizontalAlignment = HorizontalAlignment.Left };
        _last = new TextBox { Text = m.LastName };
        _nick = new TextBox { Text = m.Nickname };
        _radio = new TextBox { Text = m.RadioNumber };
        Ui.AddRow(ng, 0, "First *", _first);
        Ui.AddRow(ng, 0, "M.I.", Ui.M(_mi, 0, 0, 0, 0), 2);
        ((FrameworkElement)ng.Children[^2]).Margin = new Thickness(12, 3, 10, 3);
        Ui.AddRow(ng, 1, "Last *", _last);
        Ui.AddRow(ng, 2, "Nickname", _nick);
        Ui.AddRow(ng, 3, "Radio #", _radio);
        var opt = Ui.M(Ui.Text("(optional)", Lbl.Sub), 12, 3, 0, 3);
        Grid.SetRow(opt, 3); Grid.SetColumn(opt, 2); Grid.SetColumnSpan(opt, 2);
        ng.Children.Add(opt);
        d.Children.Add(Ui.Group("Name", ng));

        // --- Membership ---
        var sg = Ui.FormGrid();
        _status = new ComboBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        _status.Items.Add("Active");
        _status.Items.Add("Inactive");
        _status.SelectedIndex = isActive ? 0 : 1;
        _join = new DateBox(m.JoinDate);
        _inactive = new DateBox(isActive ? "" : m.InactiveDate) { IsEnabled = !isActive };
        _status.SelectionChanged += (_, _) => OnStatusChange();
        Ui.AddRow(sg, 0, "Status", _status);
        Ui.AddRow(sg, 1, "Join date", _join);
        Ui.AddRow(sg, 2, "Inactive date", _inactive);
        var note = Ui.M(Ui.Text("Dates use MM/DD/YYYY — type the date. Join date can't be in the future; inactive date " +
                                "must be on/after the join date and applies only to inactive members.", Lbl.Sub, true), 0, 6, 0, 0);
        Grid.SetRow(note, 3); Grid.SetColumnSpan(note, 2);
        sg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sg.Children.Add(note);
        d.Children.Add(Ui.Group("Membership", sg));

        // --- service time + milestone ---
        var join = m.JoinDate.Trim();
        string? end = isActive ? null : m.InactiveDate.Trim();
        var svc = DateUtil.FormatServiceTime(join, end);
        var (emblem, years) = DateUtil.MilestoneEmblem(join, end);
        var sp = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        var cap = Ui.Text("Time of service" + (isActive ? "" : " (until inactive)"), Lbl.FieldLbl);
        Ui.Fg(cap, "AccentDark");
        sp.Children.Add(cap);
        var val = Ui.Text(string.IsNullOrEmpty(svc) ? "—" : svc);
        Ui.Font(val, "FontSvcValue", true); Ui.Fg(val, "AccentDark");
        sp.Children.Add(val);
        if (!string.IsNullOrEmpty(emblem))
        {
            var ms = Ui.Text($"{emblem}  {years}-year service milestone");
            Ui.Font(ms, "FontMilestone", true); Ui.Fg(ms, "Milestone");
            sp.Children.Add(ms);
        }
        else
        {
            var ms = Ui.Text("No 5-year milestone reached yet.");
            Ui.Font(ms, "FontSmall"); Ui.Fg(ms, "Inactive");
            ms.FontStyle = FontStyles.Italic;
            sp.Children.Add(ms);
        }
        var svcBox = new Border { BorderThickness = new Thickness(1), Child = sp, Margin = new Thickness(0, 10, 0, 0) };
        svcBox.SetResourceReference(Border.BorderBrushProperty, "AccentDark");
        Ui.Bg(svcBox, "SvcBg");
        d.Children.Add(svcBox);

        // --- statistics ---
        var totals = Db.MemberAttendanceTotals(m.Id);
        d.Children.Add(Ui.Group("Attendance statistics (all time)", Ui.StatRow(
            (totals.MeetingsAttended.ToString(), "Total meetings"),
            (totals.CallsAttended.ToString(), "Total calls (weighted)"))));

        // --- buttons ---
        var btns = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        var save = Ui.Button("💾 Save Changes", SaveMember, "AccentButton");
        var del = Ui.Button("🗑 Delete Member", DeleteMember, "DangerButton");
        DockPanel.SetDock(save, Dock.Left);
        DockPanel.SetDock(del, Dock.Right);
        btns.Children.Add(save);
        btns.Children.Add(del);
        d.Children.Add(btns);
    }

    private void OnStatusChange()
    {
        if ((string)_status.SelectedItem == "Inactive")
        {
            _inactive.IsEnabled = true;
            if (_inactive.UsText.Length == 0) _inactive.SetIso(DateUtil.Today());
        }
        else
        {
            _inactive.SetIso("");
            _inactive.IsEnabled = false;
        }
    }

    private void SaveMember()
    {
        if (_detailId is not long mid) return;
        var first = _first.Text.Trim();
        var last = _last.Text.Trim();
        if (first.Length == 0 || last.Length == 0)
        {
            Ui.Warn("Save member", "First name and last name are both required.");
            return;
        }
        string join, inactiveTyped;
        try { join = _join.GetIso(); }
        catch (FormatException e) { Ui.Error("Cannot save dates", $"Join date: {e.Message}"); return; }
        try { inactiveTyped = _inactive.GetIso(); }
        catch (FormatException e) { Ui.Error("Cannot save dates", $"Inactive date: {e.Message}"); return; }
        var active = (string)_status.SelectedItem == "Active";
        try
        {
            Db.UpdateMember(mid, first: first, last: last, mi: _mi.Text.Trim(), nick: _nick.Text.Trim(),
                            active: active, radio: _radio.Text.Trim());
        }
        catch (ValidationException e) { Ui.Error("Cannot save member", e.Message); return; }

        var member = Db.GetMember(mid);
        var inactive = member is { Active: true } ? "" : (inactiveTyped.Length > 0 ? inactiveTyped : member?.InactiveDate ?? "");
        try { Db.UpdateMemberDates(mid, join, inactive); }
        catch (ValidationException e) { Ui.Error("Cannot save dates", e.Message); return; }
        Refresh();
        _app.AttendanceTab.Refresh();
        Select(mid);
    }

    private void DeleteMember()
    {
        if (_detailId is not long mid) return;
        var member = Db.GetMember(mid);
        if (member == null) return;
        if (!Ui.Confirm("Delete member", $"Delete '{member.Name}' and ALL their attendance records?\nThis cannot be undone.")) return;
        Db.DeleteMember(mid);
        _detailId = null;
        Refresh();
        _app.AttendanceTab.Refresh();
        ShowOverview();
    }

    private void AddMember()
    {
        var dlg = new AddMemberDialog(Window.GetWindow(this));
        dlg.ShowDialog();
        if (dlg.NewId is long id)
        {
            Refresh();
            _app.AttendanceTab.Refresh();
            Select(id);
        }
    }
}
