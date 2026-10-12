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

    public static TextBlock Bold(string text, bool wrap = false)
    {
        var t = Ui.Text(text, Lbl.Normal, wrap);
        t.FontWeight = FontWeights.Bold;
        return t;
    }
}

/// <summary>Edit the header/footer templates (rich text + signature image) that wrap every meeting's minutes.</summary>
public sealed class MinutesTemplateDialog : Window
{
    private readonly AppSettings _s = App.Settings;
    private readonly RichTextEditor _header, _footer;
    private readonly TextBlock _sigLbl = Ui.Text("", Lbl.Sub);
    private readonly Button _sigRemove;

    public MinutesTemplateDialog(Window? owner)
    {
        Ui.SetupDialog(this, "Minutes Header / Footer", owner, 640, 780);
        MinWidth = 600; MinHeight = 740;
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
        Row(Ui.M(Ui.Text("These wrap the minutes you type for each meeting. You can format the text (B / I / U), and " +
                         "these placeholders fill in automatically:", Lbl.Sub, true), 0, 0, 0, 4));
        var ph = Ui.Text("{attendees} = members present    {date} = meeting date    {location} = location", Lbl.Sub, true);
        ph.FontStyle = FontStyles.Italic;
        Row(Ui.M(ph, 0, 0, 0, 12));

        Row(DialogLayout.Bold("Header (appears above the minutes):"));
        _header = new RichTextEditor(10, 6, spell: true);
        Row(Ui.M(_header, 0, 3, 0, 12), true);
        _header.SetContent(_s.MinutesHeader);

        Row(DialogLayout.Bold("Footer (appears below the minutes) — click “Insert signature”, then drag it onto the signature line:", true));
        var sig = _s.SignaturePath();
        _footer = new RichTextEditor(10, 6, allowSignature: true, signaturePath: sig.Length > 0 ? sig : null, spell: true);
        Row(Ui.M(_footer, 0, 3, 0, 12), true);
        _footer.SetContent(_s.MinutesFooter);

        var sp = new StackPanel();
        sp.Children.Add(Ui.M(Ui.Text("Upload a signature image, then use “Insert signature” in the footer above and drag it " +
                                     "onto the signature line. Its position is saved and used on every exported meeting's minutes.",
                                     Lbl.Sub, true), 0, 0, 0, 6));
        var sr = new StackPanel { Orientation = Orientation.Horizontal };
        sr.Children.Add(Ui.M(_sigLbl, 0, 0, 10, 0));
        sr.Children.Add(Ui.Button("Choose Image…", ChooseSignature, "GhostButton"));
        _sigRemove = Ui.M(Ui.Button("Remove", RemoveSignature, "GhostButton"), 6, 0, 0, 0);
        sr.Children.Add(_sigRemove);
        sp.Children.Add(sr);
        var grp = Ui.Group("Signature Image", sp);
        grp.Margin = new Thickness(0, 2, 0, 8);
        Row(grp);
        RefreshSigLabel();

        root.Children.Add(g);
        Content = root;
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
        _footer.SetSignaturePath(stored);
        RefreshSigLabel();
    }

    private void RemoveSignature()
    {
        _s.ClearSignature();
        _footer.RemoveSignature();
        _footer.SetSignaturePath("");
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

/// <summary>Write / edit one meeting's minutes, with formatted header/footer previews and Word / text export.</summary>
public sealed class MinutesDialog : Window
{
    private readonly Database _db = App.Db;
    private readonly AppSettings _s = App.Settings;
    private readonly long _eventId;
    private readonly long _attendees;
    private readonly string _dateIso, _location;
    private readonly RichTextEditor _editor;

    public MinutesDialog(Window? owner, Event ev)
    {
        Ui.SetupDialog(this, "Meeting Minutes", owner, 700, 720);
        MinWidth = 600; MinHeight = 560;
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
        Row(Ui.M(Ui.Text(subtitle, Lbl.Sub), 0, 0, 0, 12));

        var sig = _s.SignaturePath();
        var head = Subst(_s.MinutesHeader);
        if (!RichTextModel.IsEmpty(RichTextModel.Encode(head)))
        {
            Row(Ui.Text("Header preview:", Lbl.Sub));
            var hp = new RichTextEditor(9, 4, readOnly: true, spell: false);
            hp.SetContent(RichTextModel.Encode(head));
            Row(Ui.M(hp, 0, 2, 0, 8));
        }

        Row(DialogLayout.Bold("Minutes:  (select text, then B / I / U — or Ctrl+B / Ctrl+I / Ctrl+U)"));
        _editor = new RichTextEditor(11, 14, spell: true);
        _editor.SetContent(_db.GetEventMinutes(_eventId));
        Row(Ui.M(_editor, 0, 3, 0, 8), true);

        var foot = Subst(_s.MinutesFooter);
        if (!RichTextModel.IsEmpty(RichTextModel.Encode(foot)) || RichTextModel.HasSignature(_s.MinutesFooter))
        {
            Row(Ui.Text("Footer preview (with signature):", Lbl.Sub));
            var fp = new RichTextEditor(9, 4, readOnly: true, signaturePath: sig.Length > 0 ? sig : null, spell: false);
            fp.SetContent(RichTextModel.Encode(foot));
            Row(Ui.M(fp, 0, 2, 0, 10));
        }

        root.Children.Add(g);
        Content = root;
        Loaded += (_, _) => _editor.Box.Focus();
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
        };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FileName;
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
