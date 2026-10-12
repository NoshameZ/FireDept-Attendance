using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using FireDeptAttendance.Core;
using FireDeptAttendance.Services;

namespace FireDeptAttendance.Controls;

public enum PageRegionKind
{
    /// <summary>Typed into directly on the page.</summary>
    Editable,
    /// <summary>Shown exactly as exported, but not editable here (e.g. header/footer while writing minutes).</summary>
    ReadOnly,
    /// <summary>Grayed placeholder text that is not part of what gets saved (e.g. sample minutes in the template).</summary>
    Sample,
}

/// <summary>
/// A simple Word-like "print layout" editor: a ribbon (B / I / U, Insert signature, Spell check, Zoom) above a
/// gray scrolling desk holding a white US-Letter page. The page size, 1" margins, Calibri 11pt and single
/// spacing with no paragraph gaps come from <see cref="MinutesPageLayout"/>, the same numbers
/// <see cref="DocxExporter"/> writes, so the page looks like the exported document while you type.
/// Regions (header, minutes, footer) stack in export order with Word-style dashed separators, and dashed
/// page-break markers show where each new page starts.
/// </summary>
public sealed class PageEditor : DockPanel
{
    private const double Dip = 96.0;
    public static readonly double PageWidth = MinutesPageLayout.PageWidthInches * Dip;     // 816
    public static readonly double PageHeight = MinutesPageLayout.PageHeightInches * Dip;   // 1056
    public static readonly double PageMargin = MinutesPageLayout.MarginInches * Dip;       // 96
    public static readonly double ContentWidth = MinutesPageLayout.ContentWidthInches * Dip;   // 624
    public static readonly double ContentHeight = MinutesPageLayout.ContentHeightInches * Dip; // 864
    public static readonly double FontPx = MinutesPageLayout.BasePt * Dip / 72.0;
    public static readonly double SignatureWidthPx = MinutesPageLayout.SignatureWidthInches * Dip;
    private const double DeskGap = 24;

    private static readonly double[] ZoomLevels = { 0.5, 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0 };
    private const string FitLabel = "Page width";

    private static readonly Brush SepBrush = Frozen(Color.FromRgb(0x9A, 0xA5, 0xB1));
    private static readonly Brush TagBg = Frozen(Color.FromRgb(0xEE, 0xF1, 0xF5));
    private static readonly Brush TagFg = Frozen(Color.FromRgb(0x5A, 0x65, 0x73));
    private static readonly Brush BreakBrush = Frozen(Color.FromRgb(0xC0, 0x50, 0x4D));
    private static readonly Brush SampleFg = Frozen(Color.FromRgb(0x9A, 0x9A, 0x9A));

    private readonly ScrollViewer _scroll;
    private readonly Grid _pageHost;
    private readonly Border _sheet, _shadow;
    private readonly StackPanel _content = new() { Width = ContentWidth, VerticalAlignment = VerticalAlignment.Top };
    private readonly Canvas _breaks = new() { IsHitTestVisible = false, ClipToBounds = false };
    private readonly ScaleTransform _zoom = new(1, 1);
    private readonly ComboBox _zoomBox;
    private readonly TextBlock _pagesLbl;
    private readonly Button _sigBtn;
    private readonly CheckBox _spellChk;
    private readonly List<Button> _fmtButtons = new();
    private readonly List<RichBoxCore> _cores = new();
    private readonly List<RichBoxCore> _editable = new();
    private RichBoxCore? _active, _sigCore;
    private bool _fitWidth = true;
    private string _sigPath;

    public int PageCount { get; private set; } = 1;

    public PageEditor(string? signaturePath, bool spell = true)
    {
        _sigPath = signaturePath ?? "";
        LastChildFill = true;

        // ---------------------------------------------------------------- ribbon
        var ribbon = new DockPanel { LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(FmtButton("B", "Bold (Ctrl+B)", EditingCommands.ToggleBold, b => b.FontWeight = FontWeights.Bold));
        left.Children.Add(FmtButton("I", "Italic (Ctrl+I)", EditingCommands.ToggleItalic, b => b.FontStyle = FontStyles.Italic));
        left.Children.Add(FmtButton("U", "Underline (Ctrl+U)", EditingCommands.ToggleUnderline, b =>
            b.Content = new TextBlock { Text = "U", TextDecorations = TextDecorations.Underline }));
        left.Children.Add(RibbonSep());
        _sigBtn = Ui.Button("✍ Insert signature", InsertSignature);
        _sigBtn.Padding = new Thickness(8, 2, 8, 2);
        _sigBtn.Focusable = false;
        _sigBtn.ToolTip = "Place the signature image at the cursor in the footer (or move it there). You can also drag it.";
        left.Children.Add(_sigBtn);
        left.Children.Add(RibbonSep());
        _spellChk = new CheckBox { Content = "Spell check", IsChecked = spell, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        _spellChk.Checked += (_, _) => ApplySpell();
        _spellChk.Unchecked += (_, _) => ApplySpell();
        left.Children.Add(_spellChk);
        SetDock(left, Dock.Left);
        ribbon.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _pagesLbl = Ui.Text("1 page", Lbl.Sub);
        _pagesLbl.VerticalAlignment = VerticalAlignment.Center;
        _pagesLbl.Margin = new Thickness(0, 0, 14, 0);
        _pagesLbl.ToolTip = "Approximate number of pages in the exported Word document";
        right.Children.Add(_pagesLbl);
        right.Children.Add(Ui.M(Ui.Text("Zoom", Lbl.Sub).Also(t => t.VerticalAlignment = VerticalAlignment.Center), 0, 0, 6, 0));
        right.Children.Add(ZoomStepButton("−", -1));
        _zoomBox = new ComboBox { Width = 104, Focusable = false, Margin = new Thickness(4, 0, 4, 0) };
        foreach (var z in ZoomLevels) _zoomBox.Items.Add($"{z * 100:0}%");
        _zoomBox.Items.Add(FitLabel);
        _zoomBox.SelectedItem = FitLabel;
        _zoomBox.SelectionChanged += (_, _) => OnZoomSelected();
        right.Children.Add(_zoomBox);
        right.Children.Add(ZoomStepButton("+", +1));
        SetDock(right, Dock.Right);
        ribbon.Children.Add(right);

        var ribbonBorder = new Border
        {
            Child = ribbon, Padding = new Thickness(8, 6, 8, 6), BorderThickness = new Thickness(1, 1, 1, 0),
            CornerRadius = new CornerRadius(4, 4, 0, 0),
        };
        ribbonBorder.SetResourceReference(Border.BackgroundProperty, "Panel");
        ribbonBorder.SetResourceReference(Border.BorderBrushProperty, "Border");
        SetDock(ribbonBorder, Dock.Top);
        Children.Add(ribbonBorder);

        // ---------------------------------------------------------------- page
        var pageGrid = new Grid();
        pageGrid.Children.Add(_content);
        pageGrid.Children.Add(_breaks);
        _sheet = new Border
        {
            Background = Brushes.White, Width = PageWidth, Height = PageHeight,
            Padding = new Thickness(PageMargin), Child = pageGrid, Cursor = Cursors.IBeam,
        };
        TextOptions.SetTextFormattingMode(_sheet, TextFormattingMode.Ideal);   // print-like glyph widths
        _sheet.MouseLeftButtonDown += OnSheetClick;
        // The drop shadow lives on a separate border behind the page so the text keeps crisp rendering.
        _shadow = new Border
        {
            Background = Brushes.White, Width = PageWidth, Height = PageHeight, IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black },
        };
        _pageHost = new Grid
        {
            Margin = new Thickness(DeskGap), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, LayoutTransform = _zoom,
        };
        _pageHost.Children.Add(_shadow);
        _pageHost.Children.Add(_sheet);

        _scroll = new ScrollViewer
        {
            Content = _pageHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
            Background = Frozen(Color.FromRgb(0xC8, 0xCC, 0xD2)),
        };
        _scroll.PreviewMouseWheel += OnWheel;
        _scroll.SizeChanged += (_, _) => { if (_fitWidth) ApplyZoom(FitScale()); };
        var deskBorder = new Border { Child = _scroll, BorderThickness = new Thickness(1) };
        deskBorder.SetResourceReference(Border.BorderBrushProperty, "Border");
        Children.Add(deskBorder);

        _content.SizeChanged += (_, _) => UpdatePages();
        UpdateRibbon();
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Separator RibbonSep() => new() { Width = 1, Margin = new Thickness(8, 2, 8, 2) };

    private Button FmtButton(string label, string tip, RoutedUICommand cmd, Action<Button> style)
    {
        var b = new Button
        {
            Content = label, Width = 30, Padding = new Thickness(0, 2, 0, 2), Margin = new Thickness(0, 0, 4, 0),
            Focusable = false, ToolTip = tip,
        };
        style(b);
        b.Click += (_, _) => Target()?.Execute(cmd);
        _fmtButtons.Add(b);
        return b;
    }

    private Button ZoomStepButton(string label, int dir)
    {
        var b = new Button { Content = label, Width = 26, Padding = new Thickness(0, 1, 0, 1), Focusable = false,
                             ToolTip = dir > 0 ? "Zoom in (Ctrl + mouse wheel)" : "Zoom out (Ctrl + mouse wheel)" };
        b.Click += (_, _) => StepZoom(dir);
        return b;
    }

    // ------------------------------------------------------------------ regions
    /// <summary>Add the next region of the page (in export order) and return its editing core.</summary>
    public RichBoxCore AddRegion(string label, string tooltip, List<List<RichRun>> paras, PageRegionKind kind,
                                 bool allowSignature = false)
    {
        _content.Children.Add(RegionSeparator(label, tooltip));
        var box = new RichTextBox
        {
            Width = ContentWidth, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = kind == PageRegionKind.Sample ? SampleFg : Brushes.Black,
            CaretBrush = Brushes.Black, SelectionBrush = SystemColors.HighlightBrush,
            FontFamily = new FontFamily(MinutesPageLayout.FontName), FontSize = FontPx,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FocusVisualStyle = null, ToolTip = null,
        };
        if (kind == PageRegionKind.Sample) box.FontStyle = FontStyles.Italic;
        var editable = kind == PageRegionKind.Editable;
        if (!editable)
        {
            box.Focusable = false;
            box.IsHitTestVisible = false;
        }
        var core = new RichBoxCore(box, !editable, editable && allowSignature, _sigPath)
        {
            DocumentPadding = new Thickness(0),
            SignatureWidth = SignatureWidthPx,
            SignatureMargin = new Thickness(0),
        };
        core.SetParagraphs(paras);
        _content.Children.Add(box);
        _cores.Add(core);
        if (editable)
        {
            _editable.Add(core);
            if (allowSignature && _sigCore == null) _sigCore = core;
            box.GotKeyboardFocus += (_, _) => _active = core;
            box.SelectionChanged += (_, _) => { if (box.IsKeyboardFocusWithin) BringCaretIntoView(box); };
            core.SetSpell(_spellChk.IsChecked == true);
        }
        UpdateRibbon();
        return core;
    }

    /// <summary>Grayed placeholder text (not saved), e.g. sample minutes between template header and footer.</summary>
    public RichBoxCore AddSample(string label, string tooltip, params string[] lines) =>
        AddRegion(label, tooltip, lines.Select(l => new List<RichRun> { new() { Text = l } }).ToList(), PageRegionKind.Sample);

    /// <summary>Word-style dashed region boundary with a small tag in the left margin. Takes no vertical space.</summary>
    private static FrameworkElement RegionSeparator(string label, string tooltip)
    {
        var c = new Canvas { Height = 0, ClipToBounds = false };
        c.Children.Add(new Line
        {
            X1 = -PageMargin + 4, X2 = ContentWidth + PageMargin - 4, Y1 = 0, Y2 = 0, Stroke = SepBrush,
            StrokeThickness = 0.75, StrokeDashArray = new DoubleCollection { 4, 3 }, SnapsToDevicePixels = true,
            IsHitTestVisible = false,
        });
        var tag = new Border
        {
            Background = TagBg, BorderBrush = SepBrush, BorderThickness = new Thickness(0.75),
            Padding = new Thickness(4, 0, 4, 1), ToolTip = tooltip, Cursor = Cursors.Arrow,
            Child = new TextBlock
            {
                Text = label, FontFamily = new FontFamily("Segoe UI"), FontSize = 10.5, Foreground = TagFg,
                MaxWidth = PageMargin - 16, TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        Canvas.SetLeft(tag, -PageMargin + 6);
        Canvas.SetTop(tag, 2);
        c.Children.Add(tag);
        return c;
    }

    // ------------------------------------------------------------------ ribbon actions
    private RichBoxCore? Target() => _active ?? _editable.FirstOrDefault();

    private void ApplySpell()
    {
        foreach (var c in _editable) c.SetSpell(_spellChk.IsChecked == true);
    }

    private void InsertSignature()
    {
        if (_sigCore == null) return;
        _sigCore.InsertSignature();
        _active = _sigCore;
    }

    private void UpdateRibbon()
    {
        var any = _editable.Count > 0;
        foreach (var b in _fmtButtons) b.IsEnabled = any;
        _spellChk.IsEnabled = any;
        _sigBtn.Visibility = _sigCore != null ? Visibility.Visible : Visibility.Collapsed;
        _sigBtn.IsEnabled = _sigCore?.HasUsableSignatureFile == true;
    }

    /// <summary>Change the signature image file and re-draw any signature already on the page.</summary>
    public void SetSignaturePath(string? path)
    {
        _sigPath = path ?? "";
        foreach (var c in _cores)
        {
            var hadSig = c.HasSignatureImage;
            var paras = hadSig ? c.GetParagraphs() : null;
            c.SetSignaturePath(_sigPath);
            if (paras != null) c.SetParagraphs(paras);
        }
        UpdateRibbon();
    }

    /// <summary>Put the caret at the end of <paramref name="core"/> and focus it.</summary>
    public void FocusRegion(RichBoxCore core)
    {
        if (!_editable.Contains(core)) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            core.Box.Focus();
            core.Box.CaretPosition = core.Box.Document.ContentEnd;
            _active = core;
            BringCaretIntoView(core.Box);
        }));
    }

    // ------------------------------------------------------------------ page clicks / scrolling
    /// <summary>A click on empty page space (margins, below the text) moves the caret to the nearest editable spot.</summary>
    private void OnSheetClick(object sender, MouseButtonEventArgs e)
    {
        if (_editable.Count == 0) return;
        RichBoxCore? best = null;
        double bestDist = double.MaxValue;
        foreach (var c in _editable)
        {
            var p = e.GetPosition(c.Box);
            var dist = p.Y < 0 ? -p.Y : p.Y > c.Box.ActualHeight ? p.Y - c.Box.ActualHeight : 0;
            if (dist < bestDist) { bestDist = dist; best = c; }
        }
        if (best == null) return;
        var box = best.Box;
        var raw = e.GetPosition(box);
        box.Focus();
        if (raw.Y > box.ActualHeight) box.CaretPosition = box.Document.ContentEnd;
        else if (raw.Y < 0) box.CaretPosition = box.Document.ContentStart;
        else
        {
            var pt = new Point(Math.Clamp(raw.X, 0, Math.Max(0, box.ActualWidth - 1)), raw.Y);
            box.CaretPosition = box.GetPositionFromPoint(pt, true) ?? box.Document.ContentEnd;
        }
        _active = best;
        e.Handled = true;
    }

    private void BringCaretIntoView(RichTextBox box)
    {
        box.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                var r = box.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
                if (r.IsEmpty || double.IsInfinity(r.Height)) return;
                box.BringIntoView(new Rect(Math.Max(0, r.X - 20), Math.Max(0, r.Y - 8), 40, r.Height + 16));
            }
            catch { /* layout not ready */ }
        }));
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            StepZoom(e.Delta > 0 ? 1 : -1);
        else
            // The page's text boxes would otherwise swallow the wheel; scroll the desk instead.
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - e.Delta * 0.5);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ zoom
    private double FitScale()
    {
        var avail = _scroll.ActualWidth - SystemParameters.VerticalScrollBarWidth - 2 * DeskGap - 4;
        if (avail <= 0) return 1;
        return Math.Clamp(avail / PageWidth, ZoomLevels[0], ZoomLevels[^1]);
    }

    private void OnZoomSelected()
    {
        if (_zoomBox.SelectedItem is not string s) return;
        _fitWidth = s == FitLabel;
        if (_fitWidth) ApplyZoom(FitScale());
        else if (double.TryParse(s.TrimEnd('%'), out var pct)) ApplyZoom(pct / 100.0);
    }

    private void StepZoom(int dir)
    {
        var cur = _zoom.ScaleX;
        double next = dir > 0
            ? ZoomLevels.FirstOrDefault(z => z > cur + 0.001, ZoomLevels[^1])
            : ZoomLevels.LastOrDefault(z => z < cur - 0.001, ZoomLevels[0]);
        _zoomBox.SelectedIndex = Array.IndexOf(ZoomLevels, next);   // triggers OnZoomSelected
    }

    private void ApplyZoom(double scale)
    {
        if (Math.Abs(_zoom.ScaleX - scale) < 0.0005) return;
        _zoom.ScaleX = _zoom.ScaleY = scale;
    }

    public double Zoom => _zoom.ScaleX;

    // ------------------------------------------------------------------ pages
    /// <summary>Grow the sheet in whole pages and mark where each new page starts.</summary>
    private void UpdatePages()
    {
        var h = _content.ActualHeight;
        var pages = Math.Max(1, (int)Math.Ceiling((h - 0.5) / ContentHeight));
        var sheetH = pages * ContentHeight + 2 * PageMargin;
        _sheet.Height = _shadow.Height = sheetH;
        if (pages == PageCount && _breaks.Children.Count == (pages - 1) * 2) return;
        PageCount = pages;
        _pagesLbl.Text = pages == 1 ? "1 page" : $"{pages} pages";
        _breaks.Children.Clear();
        for (var k = 1; k < pages; k++)
        {
            var y = k * ContentHeight;
            _breaks.Children.Add(new Line
            {
                X1 = -PageMargin, X2 = ContentWidth + PageMargin, Y1 = y, Y2 = y, Stroke = BreakBrush,
                StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 6, 4 }, SnapsToDevicePixels = true,
            });
            var lbl = new TextBlock
            {
                Text = $"Page {k + 1}", FontFamily = new FontFamily("Segoe UI"), FontSize = 10.5, Foreground = BreakBrush,
            };
            Canvas.SetLeft(lbl, ContentWidth + 8);
            Canvas.SetTop(lbl, y + 2);
            _breaks.Children.Add(lbl);
        }
    }
}
