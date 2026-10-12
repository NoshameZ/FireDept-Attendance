using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FireDeptAttendance.Controls;
using FireDeptAttendance.Core;
using FireDeptAttendance.Services;

namespace FireDeptAttendance.Dialogs;

/// <summary>
/// Create OR edit a meeting / call, with an inline multi-column attendance grid so every
/// eligible member is visible at once. Create mode: pass <c>ev = null</c>; on success
/// <see cref="SavedId"/> is the new id. Edit mode: <see cref="SavedId"/> is the event id,
/// or <see cref="Deleted"/> is true if it was removed.
/// </summary>
public sealed class EventDialog : Window
{
    private readonly Database _db = App.Db;
    private readonly AppSettings _settings = App.Settings;
    private readonly Event? _event;
    private readonly bool _isEdit;

    private readonly ComboBox _type = new() { Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _cat = new() { Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly DateBox _date;
    private readonly ToggleButton[] _mult = new ToggleButton[3];
    private readonly TextBox _loc = new();
    private readonly ComboBox _ctype = new() { IsEditable = true, IsTextSearchEnabled = true };
    private readonly TextBlock _qHint = Ui.Text("", Lbl.Sub);
    private readonly TextBox _filter = new() { Width = 220 };
    private readonly TextBlock _count = Ui.Text("", Lbl.Sub);
    private readonly Grid _inner = new();
    private readonly DockPanel _root;

    private string _callTitle;
    private string _autoLoc;
    private int _multValue;
    private bool _committing, _syncTitle;
    private string? _lastCommitDate;
    private Dictionary<long, bool> _existing = new();
    private List<Member> _members = new();
    private Dictionary<long, bool> _checked = new();
    private int _ncols = 3;

    public long? SavedId { get; private set; }
    public bool Deleted { get; private set; }

    public EventDialog(Window? owner, Event? ev)
    {
        _event = ev;
        _isEdit = ev != null;
        var titleTxt = _isEdit ? "Edit Call / Meeting" : "Add New Call / Meeting";
        Ui.SetupDialog(this, titleTxt, owner, 760, 620);
        MinWidth = 600; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // ---- initial values (mirrors the Python dialog) ----
        var initType = _isEdit ? ev!.EventType : "call";
        var initCat = _isEdit && !string.IsNullOrEmpty(ev!.CallCategory) ? ev.CallCategory! : "in_town";
        var initDate = _isEdit ? ev!.EventDate : DateUtil.Today();
        var initTitle = _isEdit ? ev!.Title ?? "" : "";
        if (initType == "meeting") initTitle = "Meetings";
        _callTitle = initType == "meeting" ? "" : initTitle;
        string initLoc;
        if (_isEdit && !string.IsNullOrEmpty(ev!.Location)) initLoc = ev.Location;
        else if (!_isEdit && initType == "meeting") initLoc = _settings.DefaultMeetingLocation;
        else if (!_isEdit && initTitle.Length > 0) initLoc = LocationFor(initTitle);
        else initLoc = "";
        var initMult = _isEdit ? ev!.CallMultiplier : 1;
        _multValue = initMult is 1 or 2 or 3 ? initMult : 1;
        _autoLoc = initLoc;

        // ---- layout ----
        (_root, var btns) = DialogLayout.Make();
        _root.Margin = new Thickness(16);
        var header = Ui.Text(titleTxt);
        header.FontSize = 18; header.FontWeight = FontWeights.Bold;
        header.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(header, Dock.Top);
        _root.Children.Add(header);

        // details
        var frm = new Grid();
        frm.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        frm.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        frm.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        frm.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _type.Items.Add("Meeting"); _type.Items.Add("Call");
        _type.SelectedItem = initType == "meeting" ? "Meeting" : "Call";
        _cat.Items.Add("In Town"); _cat.Items.Add("Out of Town");
        _cat.SelectedItem = initCat == "out_of_town" ? "Out of Town" : "In Town";
        _date = new DateBox(initDate);
        _date.Committed += OnDateChange;

        var multPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        for (var i = 0; i < 3; i++)
        {
            var m = i + 1;
            var tb = new ToggleButton { Content = $"{m}x", MinWidth = 44, IsChecked = m == _multValue };
            tb.SetResourceReference(StyleProperty, "MultToggle");
            tb.Click += (_, _) => SetMult(m);
            _mult[i] = tb;
            multPanel.Children.Add(tb);
        }

        _loc.Text = initLoc;
        foreach (var t in _db.GetDistinctCallTypes()) _ctype.Items.Add(t);
        _ctype.Text = initTitle;
        _ctype.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            if (!_syncTitle) ApplyDefaultLocation();
        }));
        _ctype.SelectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (!_syncTitle) ApplyDefaultLocation(); });

        AddField(frm, 0, 0, "Type:", _type);
        AddField(frm, 0, 2, "Category:", _cat);
        AddField(frm, 1, 0, "Date (MM/DD/YYYY):", _date);
        AddField(frm, 1, 2, "Multiplier:", multPanel);
        AddField(frm, 2, 0, "Location:", _loc);
        AddField(frm, 2, 2, "Call Type:", _ctype);
        frm.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _qHint.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(_qHint, 3); Grid.SetColumnSpan(_qHint, 4);
        frm.Children.Add(_qHint);

        var details = new GroupBox { Header = "Details", Content = new Border { Padding = new Thickness(8), Child = frm } };
        DockPanel.SetDock(details, Dock.Top);
        _root.Children.Add(details);

        // buttons
        DialogLayout.Add(btns, Ui.Button(_isEdit ? "💾 Save Changes" : "✚ Create", Save, "AccentButton"), Dock.Left);
        DialogLayout.Add(btns, Ui.Button("Cancel", Close), Dock.Left);
        if (_isEdit) DialogLayout.Add(btns, Ui.Button("🗑 Delete", DeleteEvent, "DangerButton"), Dock.Right);
        btns.Margin = new Thickness(0, 12, 0, 0);

        // attendance
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_count, Dock.Right);
        top.Children.Add(_count);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(Ui.Text("🔍"));
        left.Children.Add(Ui.M(_filter, 4, 0, 12, 0));
        left.Children.Add(Ui.M(Ui.Button("Select All", () => SetAll(true)), 2, 0, 2, 0));
        left.Children.Add(Ui.M(Ui.Button("Clear All", () => SetAll(false)), 2, 0, 2, 0));
        top.Children.Add(left);
        _filter.TextChanged += (_, _) => RenderMembers();

        var attBody = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        attBody.Children.Add(top);
        attBody.Children.Add(new ScrollViewer
        {
            Content = _inner,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        var att = new GroupBox
        {
            Header = "Attendance — check who attended",
            Margin = new Thickness(0, 12, 0, 0),
            Content = new Border { Padding = new Thickness(6), Child = attBody },
        };
        _root.Children.Add(att);   // last child fills

        Content = _root;
        _type.SelectionChanged += (_, _) => Toggle();

        Toggle();
        UpdateQuarterHint();
        _existing = _isEdit ? _db.GetEventAttendance(ev!.Id) : new Dictionary<long, bool>();
        ReloadMembers(initial: true);
        _lastCommitDate = _date.UsText;

        Loaded += (_, _) => FitToContent();
    }

    private static void AddField(Grid g, int row, int col, string label, FrameworkElement field)
    {
        while (g.RowDefinitions.Count <= row) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = Ui.Text(label);
        l.Margin = new Thickness(col == 0 ? 0 : 16, 4, 6, 4);
        Grid.SetRow(l, row); Grid.SetColumn(l, col);
        g.Children.Add(l);
        field.Margin = new Thickness(0, 4, 0, 4);
        field.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(field, row); Grid.SetColumn(field, col + 1);
        g.Children.Add(field);
    }

    // ---- window sizing ----
    private void FitToContent()
    {
        var wa = SystemParameters.WorkArea;
        _root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var chromeW = ActualWidth > 0 && _root.ActualWidth > 0 ? ActualWidth - _root.ActualWidth : 16;
        var chromeH = ActualHeight > 0 && _root.ActualHeight > 0 ? ActualHeight - _root.ActualHeight : 40;
        var w = Math.Min(Math.Max(_root.DesiredSize.Width + chromeW + 20, 640), wa.Width - 60);
        var h = Math.Min(Math.Max(_root.DesiredSize.Height + chromeH + 20, 460), wa.Height - 40);
        Width = w; Height = h;
        Left = wa.Left + Math.Max(0, (wa.Width - w) / 2);
        Top = wa.Top + Math.Max(0, (wa.Height - h) / 2 - 20);
    }

    // ---- form logic ----
    private string SelType() => (string?)_type.SelectedItem == "Meeting" ? "meeting" : "call";
    private string SelCategory() => (string?)_cat.SelectedItem == "Out of Town" ? "out_of_town" : "in_town";

    private void SetMult(int m)
    {
        _multValue = m;
        for (var i = 0; i < 3; i++) _mult[i].IsChecked = i + 1 == m;
    }

    private string LocationFor(string title) =>
        _settings.CallLocations.TryGetValue(title.Trim(), out var v) ? v : "";

    private void Toggle()
    {
        var isCall = SelType() == "call";
        _cat.IsEnabled = isCall;
        foreach (var tb in _mult) tb.IsEnabled = isCall;
        _syncTitle = true;
        try
        {
            if (isCall)
            {
                _ctype.IsEnabled = true;
                _ctype.Text = _callTitle;
            }
            else
            {
                if (!string.Equals(_ctype.Text.Trim(), "meetings", StringComparison.OrdinalIgnoreCase))
                    _callTitle = _ctype.Text;
                _ctype.Text = "Meetings";
                _ctype.IsEnabled = false;
            }
        }
        finally { _syncTitle = false; }
        ApplyDefaultLocation();
    }

    /// <summary>Pre-fill the location with the configured default (create mode only), unless the
    /// user typed their own (field empty or still holding the previous auto-filled value).</summary>
    private void ApplyDefaultLocation()
    {
        if (_isEdit) return;
        var newDefault = SelType() == "meeting" ? _settings.DefaultMeetingLocation : LocationFor(_ctype.Text);
        var current = _loc.Text.Trim();
        if (current.Length == 0 || current == (_autoLoc ?? "").Trim())
        {
            _loc.Text = newDefault;
            _autoLoc = newDefault;
        }
    }

    private void OnDateChange()
    {
        // DateBox already defers this out of the focus handler; guard re-entry and no-op commits.
        if (_committing || !IsLoaded) return;
        var current = _date.UsText;
        if (current == _lastCommitDate) return;
        _lastCommitDate = current;
        _committing = true;
        try
        {
            UpdateQuarterHint();
            ReloadMembers();
        }
        finally { _committing = false; }
    }

    private bool TryDate(out string iso, out DateTime dt)
    {
        dt = default;
        try { iso = _date.GetIso(); }
        catch (FormatException) { iso = ""; return false; }
        return iso.Length > 0 && DateTime.TryParseExact(iso, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt);
    }

    private void UpdateQuarterHint()
    {
        _qHint.Text = TryDate(out _, out var dt)
            ? $"↳ Will be filed under Q{Database.QuarterForMonth(dt.Month)} {dt.Year} (auto by date)"
            : "↳ Enter a valid date to set the quarter";
    }

    // ---- attendance grid ----
    private int ChooseColumns(int n)
    {
        if (n <= 0) return 1;
        var sh = SystemParameters.PrimaryScreenHeight;
        var maxRows = Math.Max(4, (int)((sh - 110 - 470) / 30));
        return Math.Min(Math.Max(2, (int)Math.Ceiling(n / (double)maxRows)), 6);
    }

    private void ReloadMembers(bool initial = false)
    {
        if (!TryDate(out var iso, out _)) return;   // leave the grid as-is until the date is valid
        var prev = _checked;
        _members = _db.GetMembersForEvent(iso);
        var next = new Dictionary<long, bool>();
        foreach (var m in _members)
            next[m.Id] = initial && _existing.TryGetValue(m.Id, out var ex) ? ex : prev.GetValueOrDefault(m.Id);
        _checked = next;
        _ncols = ChooseColumns(_members.Count);
        RenderMembers();
        // Re-fit once the window is on screen — deferred so it never runs inside a focus/click handler.
        if (!initial && IsLoaded)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (IsLoaded) FitToContent(); });
    }

    private bool Matches(Member m, string needle) =>
        needle.Length == 0 || m.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void RenderMembers()
    {
        _inner.Children.Clear();
        _inner.RowDefinitions.Clear();
        _inner.ColumnDefinitions.Clear();
        if (_members.Count == 0)
        {
            _inner.Children.Add(Ui.M(Ui.Text("No eligible members for this date.", Lbl.Sub), 6, 6, 6, 6));
            _count.Text = "";
            return;
        }
        var needle = _filter.Text.Trim();
        for (var c = 0; c < _ncols; c++)
            _inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var visible = _members.Where(m => Matches(m, needle)).ToList();
        if (visible.Count > 0)
        {
            var nrows = (int)Math.Ceiling(visible.Count / (double)_ncols);
            for (var r = 0; r < nrows; r++) _inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var i = 0; i < visible.Count; i++)
            {
                var m = visible[i];
                var id = m.Id;
                var cb = new CheckBox
                {
                    Content = m.Active ? m.Name : $"{m.Name}  (now inactive)",
                    IsChecked = _checked.GetValueOrDefault(id),
                    Margin = new Thickness(10, 3, 10, 3),
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                cb.SetResourceReference(FontSizeProperty, "FontValue");
                cb.Checked += (_, _) => { _checked[id] = true; UpdateCount(); };
                cb.Unchecked += (_, _) => { _checked[id] = false; UpdateCount(); };
                Grid.SetRow(cb, i % nrows);
                Grid.SetColumn(cb, i / nrows);
                _inner.Children.Add(cb);
            }
        }
        else
        {
            var t = Ui.M(Ui.Text("No members match the filter.", Lbl.Sub), 6, 6, 6, 6);
            Grid.SetColumnSpan(t, _ncols);
            _inner.Children.Add(t);
        }
        UpdateCount();
    }

    private void UpdateCount()
    {
        _count.Text = _checked.Count == 0 ? "" : $"{_checked.Values.Count(v => v)} selected / {_checked.Count}";
    }

    private void SetAll(bool value)
    {
        var needle = _filter.Text.Trim();
        foreach (var m in _members)
            if (Matches(m, needle)) _checked[m.Id] = value;
        RenderMembers();
    }

    // ---- persistence ----
    private void Save()
    {
        var etype = SelType();
        string? cat = etype == "call" ? SelCategory() : null;
        var mult = etype == "call" ? _multValue : 1;
        var title = etype == "meeting" ? "Meetings" : _ctype.Text;
        string dateIso;
        try { dateIso = _date.GetIso(); }
        catch (FormatException)
        {
            Ui.Error("Cannot save", "Date must be a valid date in MM/DD/YYYY format.");
            return;
        }
        long eid;
        try
        {
            if (_isEdit)
            {
                eid = _event!.Id;
                _db.UpdateEvent(eid, eventDate: dateIso, location: _loc.Text, title: title,
                                callCategory: cat, eventType: etype, callMultiplier: mult);
            }
            else
            {
                eid = _db.AddEvent(etype, dateIso, _loc.Text, title, cat, mult);
            }
        }
        catch (ValidationException ex)
        {
            Ui.Error("Cannot save", ex.Message);
            return;
        }
        if (_checked.Count > 0) _db.SaveEventAttendance(eid, _checked);
        SavedId = eid;
        Close();
    }

    private void DeleteEvent()
    {
        var ev = _event!;
        var typeLabel = ev.EventType.Length > 0 ? char.ToUpper(ev.EventType[0]) + ev.EventType[1..] : ev.EventType;
        var label = $"{typeLabel} on {DateUtil.IsoToUs(ev.EventDate)}";
        if (!string.IsNullOrEmpty(ev.Location)) label += $" @ {ev.Location}";
        if (!Ui.Confirm("Delete call / meeting",
                $"Delete this record?\n\n{label}\n\nAll attendance records for it will be removed. This cannot be undone."))
            return;
        _db.DeleteEvent(ev.Id);
        Deleted = true;
        Close();
    }
}
