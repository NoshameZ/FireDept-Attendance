using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using FireDeptAttendance.Controls;
using FireDeptAttendance.Core;
using FireDeptAttendance.Services;
using Microsoft.Win32;

namespace FireDeptAttendance.Dialogs;

/// <summary>Shared layout: header + body rows + bottom button bar.</summary>
internal static class DialogLayout
{
    public static (DockPanel Root, DockPanel Buttons) Make()
    {
        var root = new DockPanel { Margin = new Thickness(18) };
        var btns = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(btns, Dock.Bottom);
        root.Children.Add(btns);
        return (root, btns);
    }

    public static void Add(DockPanel bar, UIElement b, Dock side, double gap = 6)
    {
        if (b is FrameworkElement fe) fe.Margin = side == Dock.Right ? new Thickness(gap, 0, 0, 0) : new Thickness(0, 0, gap, 0);
        DockPanel.SetDock(b, side);
        bar.Children.Add(b);
    }

    /// <summary>Preferred dialog height, capped to the screen's work area so the buttons stay visible.</summary>
    public static double FitHeight(double preferred) =>
        Math.Max(360, Math.Min(preferred, SystemParameters.WorkArea.Height - 40));

    /// <summary>Preferred dialog width, capped to the screen's work area.</summary>
    public static double FitWidth(double preferred) =>
        Math.Max(480, Math.Min(preferred, SystemParameters.WorkArea.Width - 40));

    public static TextBlock Bold(string text, bool wrap = false)
    {
        var t = Ui.Text(text, Lbl.Normal, wrap);
        t.FontWeight = FontWeights.Bold;
        return t;
    }
}

/// <summary>
/// Edit the header/footer templates (rich text + signature image) that wrap every meeting's minutes, right on a
/// Word-like page: header and footer are typed in place, with grayed sample minutes in between.
/// </summary>
public sealed class MinutesTemplateDialog : Window
{
    private readonly AppSettings _s = App.Settings;
    private readonly PageEditor _page;
    private readonly RichBoxCore _header, _footer;
    private readonly TextBlock _sigLbl = Ui.Text("", Lbl.Sub);
    private readonly Button _sigRemove;

    public MinutesTemplateDialog(Window? owner)
    {
        Ui.SetupDialog(this, "Minutes Header / Footer", owner, DialogLayout.FitWidth(1000), DialogLayout.FitHeight(940));
        MinWidth = Math.Min(640, Width); MinHeight = Math.Min(560, Height);
        var (root, btns) = DialogLayout.Make();
        DialogLayout.Add(btns, Ui.Button("Restore Defaults", RestoreDefaults, "GhostButton"), Dock.Left);
        DialogLayout.Add(btns, Ui.Button("Save", Save, "AccentButton"), Dock.Right);
        DialogLayout.Add(btns, Ui.Button("Cancel", Close), Dock.Right);

        var g = new Grid();
        void Row(UIElement e, bool star = false)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = star ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetRow(e, g.RowDefinitions.Count - 1);
            g.Children.Add(e);
        }
        Row(Ui.M(Ui.Text("Minutes Header / Footer", Lbl.Header), 0, 0, 0, 2));
        Row(Ui.M(Ui.Text("Type the header and footer right on the page — it looks like the exported Word document. " +
                         "They wrap the minutes you type for each meeting (the gray text in the middle is only a sample). " +
                         "Format text with B / I / U; these placeholders fill in automatically:", Lbl.Sub, true), 0, 0, 0, 4));
        var ph = Ui.Text("{attendees} = members present    {date} = meeting date    {location} = location", Lbl.Sub, true);
        ph.FontStyle = FontStyles.Italic;
        Row(Ui.M(ph, 0, 0, 0, 10));

        var sig = _s.SignaturePath();
        _page = new PageEditor(sig.Length > 0 ? sig : null, spell: true);
        _header = _page.AddRegion("Header", "Header — appears above the minutes on every meeting's export. Click to edit.",
                                  RichTextModel.Parse(_s.MinutesHeader), PageRegionKind.Editable);
        _page.AddSample("Minutes", "Sample only — each meeting's own minutes go here. Not saved.",
                        "The minutes you type for each meeting appear here, between the header and the footer.",
                        "",
                        "(This gray text is only a sample and is not saved or exported.)",
                        "");
        _footer = _page.AddRegion("Footer", "Footer — appears below the minutes. Use “Insert signature”, then drag the " +
                                  "signature onto the signature line.",
                                  RichTextModel.Parse(_s.MinutesFooter), PageRegionKind.Editable, allowSignature: true);
        Row(Ui.M(_page, 0, 0, 0, 10), true);

        var sp = new StackPanel();
        sp.Children.Add(Ui.M(Ui.Text("Upload a signature image, then use “Insert signature” above and drag it onto the " +
                                     "signature line in the footer. Its position is saved and used on every exported meeting's minutes.",
                                     Lbl.Sub, true), 0, 0, 0, 6));
        var sr = new StackPanel { Orientation = Orientation.Horizontal };
        sr.Children.Add(Ui.M(_sigLbl, 0, 0, 10, 0));
        sr.Children.Add(Ui.Button("Choose Image…", ChooseSignature, "GhostButton"));
        _sigRemove = Ui.M(Ui.Button("Remove", RemoveSignature, "GhostButton"), 6, 0, 0, 0);
        sr.Children.Add(_sigRemove);
        sp.Children.Add(sr);
        var grp = Ui.Group("Signature Image", sp);
        grp.Margin = new Thickness(0, 0, 0, 4);
        Row(grp);
        RefreshSigLabel();

        root.Children.Add(g);
        Content = root;
        Loaded += (_, _) => _page.FocusRegion(_header);
    }

    private void RefreshSigLabel()
    {
        var p = _s.SignaturePath();
        _sigLbl.Text = p.Length > 0 ? $"Current: {Path.GetFileName(p)}" : "No signature image set.";
        _sigRemove.IsEnabled = p.Length > 0;
    }

    private void ChooseSignature()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose Signature Image",
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        string stored;
        try
        {
            // Validate it's a readable image before storing it.
            ImageInfo.GetSize(dlg.FileName);
            stored = _s.SetSignature(dlg.FileName);
        }
        catch { stored = ""; }
        if (stored.Length == 0)
        {
            Ui.Error("Signature", "Could not use that image file.");
            return;
        }
        _page.SetSignaturePath(stored);
        RefreshSigLabel();
    }

    private void RemoveSignature()
    {
        _s.ClearSignature();
        _footer.RemoveSignature();
        _header.RemoveSignature();
        _page.SetSignaturePath("");
        RefreshSigLabel();
    }

    private void RestoreDefaults()
    {
        _header.SetContent(AppSettings.DefaultHeader);
        _footer.SetContent(AppSettings.DefaultFooter);
    }

    private void Save()
    {
        _s.MinutesHeader = _header.GetContent();
        _s.MinutesFooter = _footer.GetContent();
        _s.Save();
        Close();
    }
}

/// <summary>
/// Write / edit one meeting's minutes on a Word-like page: the header and footer from Settings (placeholders
/// filled in, signature shown) sit above and below the minutes exactly as they will be exported.
/// Save stores the minutes; Export writes Word (.docx) or text.
/// </summary>
public sealed class MinutesDialog : Window
{
    private readonly Database _db = App.Db;
    private readonly AppSettings _s = App.Settings;
    private readonly long _eventId;
    private readonly long _attendees;
    private readonly string _dateIso, _location;
    private readonly PageEditor _page;
    private readonly RichBoxCore _editor;

    public MinutesDialog(Window? owner, Event ev)
    {
        Ui.SetupDialog(this, "Meeting Minutes", owner, DialogLayout.FitWidth(1000), DialogLayout.FitHeight(980));
        MinWidth = Math.Min(600, Width); MinHeight = Math.Min(480, Height);
        _eventId = ev.Id;
        _attendees = _db.CountEventAttendees(ev.Id);
        _dateIso = ev.EventDate ?? "";
        _location = ev.Location ?? "";

        var (root, btns) = DialogLayout.Make();
        DialogLayout.Add(btns, Ui.Button("Export…", Export, "GhostButton"), Dock.Left);
        DialogLayout.Add(btns, Ui.Button("Save", Save, "AccentButton"), Dock.Right);
        DialogLayout.Add(btns, Ui.Button("Cancel", Close), Dock.Right);

        var g = new Grid();
        void Row(UIElement e, bool star = false)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = star ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetRow(e, g.RowDefinitions.Count - 1);
            g.Children.Add(e);
        }
        Row(Ui.M(Ui.Text("Meeting Minutes", Lbl.Header), 0, 0, 0, 2));
        var subtitle = "Meeting on " + (_dateIso.Length > 0 ? DateUtil.IsoToUs(_dateIso) : "");
        if (_location.Length > 0) subtitle += $"  •  {_location}";
        subtitle += $"  •  {_attendees} in attendance";
        Row(Ui.M(Ui.Text(subtitle, Lbl.Sub), 0, 0, 0, 2));
        Row(Ui.M(Ui.Text("Type the minutes on the page between the header and footer — this is how the Word export will look. " +
                         "Ctrl+B / Ctrl+I / Ctrl+U format text; Tab inserts a tab, Shift+Tab removes one. " +
                         "The header and footer are edited in Settings → Minutes Header / Footer.", Lbl.Sub, true), 0, 0, 0, 10));

        var sig = _s.SignaturePath();
        _page = new PageEditor(sig.Length > 0 ? sig : null, spell: true);
        _page.AddRegion("Header", "Header from Settings → Minutes Header / Footer (placeholders filled in). Not editable here.",
                        Subst(_s.MinutesHeader), PageRegionKind.ReadOnly);
        _editor = _page.AddRegion("Minutes", "This meeting's minutes — type here.",
                                  RichTextModel.Parse(_db.GetEventMinutes(_eventId)), PageRegionKind.Editable);
        var foot = Subst(_s.MinutesFooter);
        // The export adds the signature at the very end when the footer doesn't place it; show it the same way.
        if (sig.Length > 0 && !RichTextModel.HasSignature(_s.MinutesFooter) && !RichTextModel.HasSignature(_s.MinutesHeader))
            foot.Add(new List<RichRun> { new() { IsImage = true } });
        _page.AddRegion("Footer", "Footer from Settings → Minutes Header / Footer (with signature). Not editable here.",
                        foot, PageRegionKind.ReadOnly);
        Row(_page, true);

        root.Children.Add(g);
        Content = root;
        Loaded += (_, _) => _page.FocusRegion(_editor);
    }

    private List<List<RichRun>> Subst(string tmpl) =>
        RichTextModel.Substitute(RichTextModel.Parse(tmpl), RichTextModel.Placeholders(_attendees, _dateIso, _location));

    private void Save()
    {
        _db.SaveEventMinutes(_eventId, _editor.GetContent());
        Close();
    }

    private void Export()
    {
        // Persist first so the file matches what's stored.
        var content = _editor.GetContent();
        _db.SaveEventMinutes(_eventId, content);
        var body = RichTextModel.Parse(content);
        var header = Subst(_s.MinutesHeader);
        var footer = Subst(_s.MinutesFooter);
        var dlg = new SaveFileDialog
        {
            Title = "Export Minutes", DefaultExt = ".docx", AddExtension = true,
            FileName = $"minutes_{(_dateIso.Length > 0 ? _dateIso : "meeting")}.docx",
            Filter = "Word document (*.docx)|*.docx|Text file (*.txt)|*.txt|All files (*.*)|*.*",
            InitialDirectory = AppSettings.ResolveDir(_s.ExportSingleMinutesDir),
        };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FileName;
        _s.ExportSingleMinutesDir = Path.GetDirectoryName(path) ?? "";
        _s.Save();
        var sig = _s.SignaturePath();
        try
        {
            if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                // A signature image can't live in a .txt file; it is skipped.
                var blocks = new[]
                {
                    RichTextModel.ToPlain(header).TrimEnd('\n'),
                    RichTextModel.ToPlain(body).Trim('\n'),
                    RichTextModel.ToPlain(footer).TrimStart('\n'),
                };
                var doc = string.Join("\n", blocks.Where(b => b.Trim().Length > 0)) + "\n";
                File.WriteAllText(path, doc, new UTF8Encoding(false));
            }
            else
            {
                DocxExporter.ExportMinutes(path, header, body, footer, sig.Length > 0 ? sig : null);
            }
        }
        catch (Exception e)
        {
            Ui.Error("Export failed", $"Could not write file:\n{e.Message}");
            return;
        }
        Ui.Info("Minutes exported", $"Saved to:\n{path}");
    }
}
