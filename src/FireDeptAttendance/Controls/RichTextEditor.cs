using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FireDeptAttendance.Core;
using FireDeptAttendance.Services;

namespace FireDeptAttendance.Controls;

/// <summary>
/// Port of richtext.RichText: a WPF RichTextBox editor with B / I / U (buttons and
/// Ctrl+B / Ctrl+I / Ctrl+U), spell check with a personal dictionary, and an
/// optional single embedded signature image that can be dragged to a new spot.
/// Content round-trips through <see cref="RichTextModel"/> ("rich-v1" JSON).
/// </summary>
public sealed class RichTextEditor : DockPanel
{
    private const double SigEditorHeight = 72;
    private static readonly string LexPath = Path.Combine(Path.GetTempPath(), "FireDeptAttendance_userwords.lex");

    public RichTextBox Box { get; }
    private readonly bool _readOnly;
    private readonly bool _allowSignature;
    private string _sigPath;
    private Button? _sigBtn;
    private CheckBox? _spellChk;
    private InlineUIContainer? _dragging;

    public RichTextEditor(double basePt, int heightLines, bool readOnly = false, bool allowSignature = false,
                          string? signaturePath = null, bool spell = true, int? minLines = null)
    {
        _readOnly = readOnly;
        _allowSignature = allowSignature;
        _sigPath = signaturePath ?? "";
        LastChildFill = true;

        Box = new RichTextBox
        {
            AcceptsReturn = true,
            AcceptsTab = !readOnly,
            IsReadOnly = readOnly,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = basePt * 96.0 / 72.0,
            IsDocumentEnabled = true,
        };
        Box.SetResourceReference(Control.FontFamilyProperty, "AppFont");
        Box.Document.PagePadding = new Thickness(2);
        var lineH = Box.FontSize * 1.45;
        Box.Height = double.NaN;
        // Editable boxes fill the space their parent gives them and scroll inside it; the minimum is kept
        // small so the box never grows past the window (which used to clip the bottom and hide the scrollbar).
        Box.MinHeight = (minLines ?? heightLines) * lineH + 12;
        if (readOnly) Box.MaxHeight = heightLines * lineH + 12;
        else Box.PreviewKeyDown += OnBoxPreviewKeyDown;

        if (!readOnly)
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            bar.Children.Add(FmtButton("B", EditingCommands.ToggleBold, b => b.FontWeight = FontWeights.Bold));
            bar.Children.Add(FmtButton("I", EditingCommands.ToggleItalic, b => b.FontStyle = FontStyles.Italic));
            bar.Children.Add(FmtButton("U", EditingCommands.ToggleUnderline, b =>
                b.Content = new TextBlock { Text = "U", TextDecorations = TextDecorations.Underline }));
            if (allowSignature)
            {
                bar.Children.Add(new Separator { Width = 1, Margin = new Thickness(8, 2, 8, 2) });
                _sigBtn = Ui.Button("Insert signature", InsertSignature);
                _sigBtn.Padding = new Thickness(8, 2, 8, 2);
                bar.Children.Add(_sigBtn);
            }
            bar.Children.Add(new Separator { Width = 1, Margin = new Thickness(8, 2, 8, 2) });
            _spellChk = new CheckBox { Content = "Spell check", IsChecked = spell };
            _spellChk.Checked += (_, _) => SetSpell(true);
            _spellChk.Unchecked += (_, _) => SetSpell(false);
            bar.Children.Add(_spellChk);
            SetDock(bar, Dock.Top);
            Children.Add(bar);

            Box.ContextMenuOpening += OnContextMenuOpening;
            Box.PreviewMouseLeftButtonUp += OnBoxMouseUp;
            Box.PreviewMouseMove += (_, e) =>
            {
                if (_dragging != null && e.LeftButton == MouseButtonState.Pressed)
                {
                    var p = Box.GetPositionFromPoint(e.GetPosition(Box), true);
                    if (p != null) Box.CaretPosition = p;
                }
            };
            SetSpell(spell);
        }
        Children.Add(Box);
        UpdateSigButton();
    }

    private Button FmtButton(string label, RoutedUICommand cmd, Action<Button> style)
    {
        var b = new Button { Content = label, Width = 30, Padding = new Thickness(0, 2, 0, 2), Margin = new Thickness(0, 0, 4, 0), Focusable = false };
        style(b);
        b.Click += (_, _) => { cmd.Execute(null, Box); Box.Focus(); };
        return b;
    }

    // ------------------------------------------------------------------ tab keys
    /// <summary>
    /// Tab inserts a real tab character (replacing any selection) instead of WPF's default of changing
    /// paragraph indentation, which the saved format cannot store. Shift+Tab removes one leading tab from
    /// each selected paragraph. Ctrl+Tab / Ctrl+Shift+Tab move keyboard focus out of the editor.
    /// </summary>
    private void OnBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || Box.IsReadOnly) return;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control))
        {
            Box.MoveFocus(new TraversalRequest(mods.HasFlag(ModifierKeys.Shift)
                ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
        }
        else if (mods == ModifierKeys.Shift) Outdent();
        else if (mods == ModifierKeys.None) InsertTab();
        else return;
        e.Handled = true;
    }

    private void InsertTab()
    {
        Box.BeginChange();
        try
        {
            Box.Selection.Text = "\t";   // replaces the selection, keeps the formatting at the caret
            Box.Selection.Select(Box.Selection.End, Box.Selection.End);
        }
        finally { Box.EndChange(); }
    }

    private void Outdent()
    {
        var first = Box.Selection.Start.Paragraph;
        var last = Box.Selection.End.Paragraph;
        if (first == null) return;
        Box.BeginChange();
        try
        {
            for (Block? b = first; b != null; b = b.NextBlock)
            {
                if (b is Paragraph p) RemoveLeadingTab(p);
                if (b == last || last == null) break;
            }
        }
        finally { Box.EndChange(); }
    }

    private static void RemoveLeadingTab(Paragraph p)
    {
        var tp = p.ContentStart;
        while (tp != null && tp.CompareTo(p.ContentEnd) < 0 && tp.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text)
        {
            // Stop at a non-text element (e.g. the signature image): it is "content" before any tab.
            if (tp.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.EmbeddedElement) return;
            tp = tp.GetNextContextPosition(LogicalDirection.Forward);
        }
        if (tp == null || tp.CompareTo(p.ContentEnd) >= 0) return;
        if (!tp.GetTextInRun(LogicalDirection.Forward).StartsWith('\t')) return;
        var end = tp.GetPositionAtOffset(1);
        if (end != null) new TextRange(tp, end).Text = "";
    }

    // ------------------------------------------------------------------ spell check
    private void SetSpell(bool on)
    {
        Box.SpellCheck.IsEnabled = on;
        if (on) RefreshDictionary();
    }

    private void RefreshDictionary()
    {
        try
        {
            var words = App.Settings?.UserWords ?? new List<string>();
            File.WriteAllLines(LexPath, new[] { "#LID 1033" }.Concat(words.Where(w => !string.IsNullOrWhiteSpace(w))));
            var uri = new Uri(LexPath);
            var dicts = SpellCheck.GetCustomDictionaries(Box);
            dicts.Clear();
            dicts.Add(uri);
        }
        catch { /* custom dictionary is optional */ }
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = new ContextMenu();
        var err = Box.SpellCheck.IsEnabled ? Box.GetSpellingError(Box.CaretPosition) : null;
        if (err != null)
        {
            var range = Box.GetSpellingErrorRange(Box.CaretPosition);
            var word = range?.Text ?? "";
            var any = false;
            foreach (var s in err.Suggestions.Take(6))
            {
                var mi = new MenuItem { Header = s, FontWeight = FontWeights.Bold };
                var sug = s;
                mi.Click += (_, _) => err.Correct(sug);
                menu.Items.Add(mi);
                any = true;
            }
            if (!any) menu.Items.Add(new MenuItem { Header = "(no suggestions)", IsEnabled = false });
            menu.Items.Add(new Separator());
            var add = new MenuItem { Header = $"Add “{word}” to dictionary" };
            add.Click += (_, _) => { App.Settings.AddUserWord(word); RefreshDictionary(); };
            menu.Items.Add(add);
            var ign = new MenuItem { Header = "Ignore" };
            ign.Click += (_, _) => err.IgnoreAll();
            menu.Items.Add(ign);
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(new MenuItem { Header = "Cut", Command = ApplicationCommands.Cut, CommandTarget = Box });
        menu.Items.Add(new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy, CommandTarget = Box });
        menu.Items.Add(new MenuItem { Header = "Paste", Command = ApplicationCommands.Paste, CommandTarget = Box });
        Box.ContextMenu = menu;
    }

    // ------------------------------------------------------------------ content
    public void SetContent(string? stored)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(2) };
        foreach (var para in RichTextModel.Parse(stored))
        {
            var p = new Paragraph { Margin = new Thickness(0) };
            foreach (var run in para)
            {
                if (run.IsImage)
                {
                    var c = MakeSigContainer();
                    if (c != null) p.Inlines.Add(c);
                    continue;
                }
                if (string.IsNullOrEmpty(run.Text)) continue;
                var r = new Run(run.Text);
                if (run.Bold) r.FontWeight = FontWeights.Bold;
                if (run.Italic) r.FontStyle = FontStyles.Italic;
                if (run.Underline) r.TextDecorations = TextDecorations.Underline;
                p.Inlines.Add(r);
            }
            doc.Blocks.Add(p);
        }
        Box.Document = doc;
        if (!_readOnly) Box.CaretPosition = doc.ContentEnd;
    }

    public string GetContent() => RichTextModel.Encode(GetParagraphs());

    public List<List<RichRun>> GetParagraphs()
    {
        var paras = new List<List<RichRun>>();
        foreach (var block in Box.Document.Blocks) CollectBlock(block, paras);
        if (paras.Count == 0) paras.Add(new List<RichRun>());
        return paras;
    }

    private static void CollectBlock(Block block, List<List<RichRun>> paras)
    {
        switch (block)
        {
            case Paragraph p:
                var cur = new List<RichRun>();
                paras.Add(cur);
                foreach (var inline in p.Inlines) CollectInline(inline, ref cur, paras);
                break;
            case List l:
                foreach (var item in l.ListItems)
                    foreach (var b in item.Blocks) CollectBlock(b, paras);
                break;
            case Section s:
                foreach (var b in s.Blocks) CollectBlock(b, paras);
                break;
            default:
                foreach (var line in new TextRange(block.ContentStart, block.ContentEnd).Text.Replace("\r", "").Split('\n'))
                    paras.Add(new List<RichRun> { new() { Text = line } });
                break;
        }
    }

    private static void CollectInline(Inline inline, ref List<RichRun> cur, List<List<RichRun>> paras)
    {
        switch (inline)
        {
            case Run r when r.Text.Length > 0:
                var underline = false;
                for (DependencyObject? o = r; o is Inline il; o = il.Parent)
                    if (il.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true) underline = true;
                var bold = r.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight();
                var italic = r.FontStyle == FontStyles.Italic || r.FontStyle == FontStyles.Oblique;
                var lines = r.Text.Replace("\r", "").Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i > 0) { cur = new List<RichRun>(); paras.Add(cur); }
                    if (lines[i].Length == 0) continue;
                    var last = cur.LastOrDefault();
                    if (last != null && !last.IsImage && last.Bold == bold && last.Italic == italic && last.Underline == underline)
                        last.Text += lines[i];
                    else
                        cur.Add(new RichRun { Text = lines[i], Bold = bold, Italic = italic, Underline = underline });
                }
                break;
            case LineBreak:
                cur = new List<RichRun>();
                paras.Add(cur);
                break;
            case InlineUIContainer c when c.Tag as string == "sig":
                cur.Add(new RichRun { IsImage = true });
                break;
            case Span s:
                foreach (var child in s.Inlines.ToList()) CollectInline(child, ref cur, paras);
                break;
        }
    }

    public bool IsEmpty => RichTextModel.IsEmpty(GetContent());

    // ------------------------------------------------------------------ signature
    public void SetSignaturePath(string? path)
    {
        _sigPath = path ?? "";
        UpdateSigButton();
    }

    private void UpdateSigButton()
    {
        if (_sigBtn != null) _sigBtn.IsEnabled = !string.IsNullOrEmpty(_sigPath) && File.Exists(_sigPath);
    }

    private Image? MakeSigImage()
    {
        if (string.IsNullOrEmpty(_sigPath) || !File.Exists(_sigPath)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // don't lock the file
            bmp.UriSource = new Uri(_sigPath);
            bmp.EndInit();
            return new Image
            {
                Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(3, 2, 3, 2),
                Height = Math.Min(SigEditorHeight, bmp.PixelHeight > 0 ? bmp.PixelHeight : SigEditorHeight),
            };
        }
        catch { return null; }
    }

    private InlineUIContainer Configure(InlineUIContainer c)
    {
        c.Tag = "sig";
        c.BaselineAlignment = BaselineAlignment.Bottom;
        if (!_readOnly && c.Child is Image img)
        {
            img.Cursor = Cursors.SizeAll;
            img.ToolTip = "Drag to move the signature";
            img.MouseLeftButtonDown += (_, e) => { _dragging = c; e.Handled = true; };
        }
        return c;
    }

    private InlineUIContainer? MakeSigContainer()
    {
        var img = MakeSigImage();
        return img == null ? null : Configure(new InlineUIContainer(img));
    }

    /// <summary>Insert a new signature image at <paramref name="pos"/>.</summary>
    private bool InsertSigAt(TextPointer pos)
    {
        var img = MakeSigImage();
        if (img == null) return false;
        var at = pos.GetInsertionPosition(LogicalDirection.Forward);
        if (at.Paragraph == null)
        {
            var c = new InlineUIContainer(img);
            Box.Document.Blocks.Add(new Paragraph(c) { Margin = new Thickness(0) });
            Configure(c);
        }
        else
        {
            Configure(new InlineUIContainer(img, at));
        }
        return true;
    }

    private IEnumerable<InlineUIContainer> SigContainers()
    {
        var list = new List<InlineUIContainer>();
        void Walk(InlineCollection inl)
        {
            foreach (var i in inl)
            {
                if (i is InlineUIContainer c && c.Tag as string == "sig") list.Add(c);
                else if (i is Span s) Walk(s.Inlines);
            }
        }
        foreach (var b in Box.Document.Blocks.OfType<Paragraph>()) Walk(b.Inlines);
        return list;
    }

    public bool HasSignatureImage => SigContainers().Any();

    public void RemoveSignature()
    {
        foreach (var c in SigContainers().ToList())
            if (c.Parent is Paragraph p) p.Inlines.Remove(c);
            else if (c.Parent is Span s) s.Inlines.Remove(c);
    }

    private void InsertSignature()
    {
        if (MakeSigImage() == null)
        {
            Ui.Info("Signature", "No signature image is set.\nAdd one in Settings → Minutes Header / Footer first.");
            return;
        }
        var offset = Box.Document.ContentStart.GetOffsetToPosition(Box.CaretPosition);
        offset -= OffsetShiftForRemoval(offset);
        RemoveSignature();
        InsertSigAt(Box.Document.ContentStart.GetPositionAtOffset(Math.Max(0, offset)) ?? Box.Document.ContentEnd);
        Box.Focus();
    }

    /// <summary>Symbols removed before <paramref name="offset"/> when the existing signature is deleted.</summary>
    private int OffsetShiftForRemoval(int offset)
    {
        var shift = 0;
        foreach (var c in SigContainers())
            if (Box.Document.ContentStart.GetOffsetToPosition(c.ElementEnd) <= offset)
                shift += c.ElementStart.GetOffsetToPosition(c.ElementEnd);
        return shift;
    }

    private void OnBoxMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging == null) return;
        var old = _dragging;
        _dragging = null;
        var drop = Box.GetPositionFromPoint(e.GetPosition(Box), true);
        if (drop == null) return;
        // Ignore drops onto the image itself.
        if (drop.CompareTo(old.ContentStart) >= 0 && drop.CompareTo(old.ContentEnd) <= 0) return;
        var anchor = drop.GetInsertionPosition(LogicalDirection.Forward);
        var offset = Box.Document.ContentStart.GetOffsetToPosition(anchor);
        offset -= OffsetShiftForRemoval(offset);
        RemoveSignature();
        InsertSigAt(Box.Document.ContentStart.GetPositionAtOffset(Math.Max(0, offset)) ?? Box.Document.ContentEnd);
        e.Handled = true;
    }
}
