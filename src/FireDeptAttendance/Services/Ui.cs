using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FireDeptAttendance.Services;

/// <summary>Label styles mirroring the ttk styles used by the Python app.</summary>
public enum Lbl { Normal, Header, Sub, SideTitle, FieldLbl, StatNum, StatCap, Value, Small }

/// <summary>Small factory helpers so the code-built UI stays compact and theme-aware.</summary>
public static class Ui
{
    public static TextBlock Text(string text, Lbl style = Lbl.Normal, bool wrap = false)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (wrap) t.TextWrapping = TextWrapping.Wrap;
        Style(t, style);
        return t;
    }

    public static void Style(TextBlock t, Lbl style)
    {
        switch (style)
        {
            case Lbl.Header: Font(t, "FontHeader", true); Fg(t, "Text"); break;
            case Lbl.Sub: Font(t, "FontSub"); Fg(t, "Muted"); break;
            case Lbl.SideTitle: Font(t, "FontSideTitle", true); Fg(t, "AccentDark"); break;
            case Lbl.FieldLbl: Font(t, "FontFieldLbl", true); Fg(t, "Text"); break;
            case Lbl.StatNum: Font(t, "FontStatNum", true); Fg(t, "AccentDark"); break;
            case Lbl.StatCap: Font(t, "FontStatCap"); Fg(t, "Muted"); break;
            case Lbl.Value: Font(t, "FontValue", true); Fg(t, "Text"); break;
            case Lbl.Small: Font(t, "FontSmall"); Fg(t, "Text"); break;
            default: Fg(t, "Text"); break;
        }
    }

    public static void Font(FrameworkElement e, string key, bool bold = false)
    {
        e.SetResourceReference(TextBlock.FontSizeProperty, key);
        if (bold) e.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
    }

    public static T Fg<T>(T e, string key) where T : FrameworkElement
    {
        if (e is Control c) c.SetResourceReference(Control.ForegroundProperty, key);
        else e.SetResourceReference(TextBlock.ForegroundProperty, key);
        return e;
    }

    public static T Bg<T>(T e, string key) where T : FrameworkElement
    {
        switch (e)
        {
            case Panel p: p.SetResourceReference(Panel.BackgroundProperty, key); break;
            case Border b: b.SetResourceReference(Border.BackgroundProperty, key); break;
            case Control c: c.SetResourceReference(Control.BackgroundProperty, key); break;
            case TextBlock t: t.SetResourceReference(TextBlock.BackgroundProperty, key); break;
        }
        return e;
    }

    public static Button Button(string text, Action onClick, string? style = null)
    {
        var b = new Button { Content = text };
        if (style != null) b.SetResourceReference(FrameworkElement.StyleProperty, style);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static T M<T>(T e, double l, double t, double r, double b) where T : FrameworkElement
    {
        e.Margin = new Thickness(l, t, r, b);
        return e;
    }

    /// <summary>A bordered card (used for stat tiles).</summary>
    public static Border Card(UIElement child, string bg = "Card")
    {
        var b = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(6, 8, 6, 8), Child = child };
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.SetResourceReference(Border.BackgroundProperty, bg);
        return b;
    }

    /// <summary>Stat tile: big accent number over a muted caption.</summary>
    public static Border StatCard(string num, string caption)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = num, HorizontalAlignment = HorizontalAlignment.Center }.Also(t => Style(t, Lbl.StatNum)));
        sp.Children.Add(new TextBlock { Text = caption, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap }.Also(t => Style(t, Lbl.StatCap)));
        return M(Card(sp), 4, 4, 4, 4);
    }

    /// <summary>A uniform grid row of stat cards.</summary>
    public static UniformGrid StatRow(params (string Num, string Cap)[] cards)
    {
        var g = new UniformGrid { Rows = 1, Columns = cards.Length };
        foreach (var (n, c) in cards) g.Children.Add(StatCard(n, c));
        return g;
    }

    public static Separator HSep(double v = 12) => new() { Margin = new Thickness(0, v, 0, v) };

    public static GroupBox Group(string header, UIElement content) =>
        new() { Header = header, Content = content, Margin = new Thickness(0, 8, 0, 0) };

    public static T Also<T>(this T obj, Action<T> act) { act(obj); return obj; }

    // ---- message boxes ----
    public static void Info(string title, string msg) => Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Information);
    public static void Warn(string title, string msg) => Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    public static void Error(string title, string msg) => Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Error);
    public static bool Confirm(string title, string msg, MessageBoxImage icon = MessageBoxImage.Warning) =>
        Show(msg, title, MessageBoxButton.YesNo, icon) == MessageBoxResult.Yes;

    private static MessageBoxResult Show(string msg, string title, MessageBoxButton b, MessageBoxImage i)
    {
        var owner = Owner();
        return owner != null ? MessageBox.Show(owner, msg, title, b, i) : MessageBox.Show(msg, title, b, i);
    }

    /// <summary>
    /// Active window, else the main window — but only if it is actually shown (at startup, before the
    /// main window exists, WPF may point MainWindow at an already-closed first-run dialog).
    /// </summary>
    private static Window? Owner()
    {
        var app = Application.Current;
        if (app == null) return null;
        var active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
        if (active != null) return active;
        return app.MainWindow is { IsVisible: true } mw ? mw : null;
    }

    /// <summary>Common dialog window setup: theme-aware, Esc closes.</summary>
    public static void SetupDialog(Window w, string title, Window? owner, double width, double height, bool resizable = true)
    {
        w.Title = title;
        w.Width = width;
        w.Height = height;
        w.Owner = owner ?? Owner();
        w.WindowStartupLocation = w.Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        w.ShowInTaskbar = false;
        w.ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        ApplyWindow(w);
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; w.Close(); } };
    }

    public static void ApplyWindow(Window w)
    {
        w.SetResourceReference(Control.BackgroundProperty, "Bg");
        w.SetResourceReference(Control.ForegroundProperty, "Text");
        w.SetResourceReference(Control.FontFamilyProperty, "AppFont");
        w.SetResourceReference(Control.FontSizeProperty, "FontBase");
        w.UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
        try { w.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/app.ico")); }
        catch { /* icon optional */ }
    }

    /// <summary>Build a two-column label/field grid row.</summary>
    public static void AddRow(Grid g, int row, string label, UIElement field, int col = 0)
    {
        while (g.RowDefinitions.Count <= row) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = Text(label, Lbl.FieldLbl);
        l.Margin = new Thickness(0, 3, 10, 3);
        Grid.SetRow(l, row); Grid.SetColumn(l, col);
        g.Children.Add(l);
        if (field is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 0, 3);
        Grid.SetRow(field, row); Grid.SetColumn(field, col + 1);
        g.Children.Add(field);
    }

    public static Grid FormGrid(int pairs = 1)
    {
        var g = new Grid();
        for (var i = 0; i < pairs; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        return g;
    }

    public static string Money(double v) => "$" + v.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
}
