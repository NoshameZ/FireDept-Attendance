using System.Windows;
using System.Windows.Media;
using FireDeptAttendance.Core;

namespace FireDeptAttendance.Services;

/// <summary>
/// Port of theming.py: builds the light/dark palette (plus accent-derived colours)
/// and publishes it as brush / font-size resources in Application.Resources.
/// Every control references them via DynamicResource, so re-applying the theme
/// updates the whole UI live.
/// </summary>
public static class ThemeManager
{
    private static readonly Dictionary<string, string> Light = new()
    {
        ["Bg"] = "#eceff4", ["Panel"] = "#ffffff", ["Card"] = "#ffffff", ["Text"] = "#1f2933",
        ["Muted"] = "#6b7280", ["Border"] = "#dde2e9", ["Milestone"] = "#b8860b",
        ["TreeBg"] = "#ffffff", ["TreeHeadBg"] = "#eef1f6", ["TreeHeadFg"] = "#374151",
        ["RowAlt"] = "#f6f8fb", ["Inactive"] = "#9aa0a6", ["BtnBg"] = "#e7ebf1",
        ["BtnFg"] = "#1f2933", ["BtnHover"] = "#d7dde6", ["Disabled"] = "#a7adb7",
        ["ScrollThumb"] = "#c3cad4",
    };

    private static readonly Dictionary<string, string> Dark = new()
    {
        ["Bg"] = "#15171f", ["Panel"] = "#20232e", ["Card"] = "#2a2e3c", ["Text"] = "#e8eaf0",
        ["Muted"] = "#9aa1b2", ["Border"] = "#363b4c", ["Milestone"] = "#f3c623",
        ["TreeBg"] = "#20232e", ["TreeHeadBg"] = "#2a2e3c", ["TreeHeadFg"] = "#cdd2e0",
        ["RowAlt"] = "#252937", ["Inactive"] = "#6f7589", ["BtnBg"] = "#323748",
        ["BtnFg"] = "#e8eaf0", ["BtnHover"] = "#3c4258", ["Disabled"] = "#646a7d",
        ["ScrollThumb"] = "#3a3f52",
    };

    /// <summary>Base point sizes from the Python app (scaled by the Font Size setting).</summary>
    private static readonly Dictionary<string, double> FontPoints = new()
    {
        ["FontBase"] = 10, ["FontHeader"] = 14, ["FontSub"] = 9, ["FontSideTitle"] = 15,
        ["FontFieldLbl"] = 9, ["FontStatNum"] = 16, ["FontStatCap"] = 9, ["FontValue"] = 11,
        ["FontToolbarTitle"] = 14, ["FontSvcValue"] = 15, ["FontMilestone"] = 13, ["FontSmall"] = 9,
    };

    public static bool IsDark { get; private set; }

    public static void Apply(AppSettings s)
    {
        var res = Application.Current.Resources;
        IsDark = s.Theme == "dark";
        var pal = new Dictionary<string, string>(IsDark ? Dark : Light);
        var accent = AppSettings.IsHex(s.Accent) ? s.Accent : "#B22222";
        pal["Accent"] = accent;
        pal["AccentDark"] = Shade(accent, 0.82);
        pal["AccentLight"] = Shade(accent, 1.15);
        pal["OnAccent"] = BestOn(accent);
        pal["AccentHoverTab"] = Mix(accent, pal["Bg"], 0.6);
        pal["TabBg"] = Mix(pal["Panel"], pal["Bg"], 0.5);
        pal["SvcBg"] = Mix(pal["Panel"], accent, IsDark ? 0.22 : 0.08);
        pal["ToolBtn"] = Shade(accent, 1.18);
        pal["ToolBtnHover"] = Shade(accent, 1.32);
        pal["ToolSep"] = Shade(accent, 1.25);
        pal["Danger"] = "#c0392b";
        pal["DangerHover"] = "#a93226";

        foreach (var (k, v) in pal)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(v));
            b.Freeze();
            res[k] = b;
            res[k + "Color"] = b.Color;
        }

        var scale = AppSettings.ScalingFor(s.FontSize);
        foreach (var (k, pt) in FontPoints)
            res[k] = Math.Round(pt * scale * 96.0 / 72.0, 1);

        res["AppFont"] = new FontFamily(string.IsNullOrWhiteSpace(s.FontFamily) ? "Segoe UI" : s.FontFamily + ", Segoe UI");
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    // ---- colour helpers (identical maths to theming.py) ----
    private static (double R, double G, double B) Rgb(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => $"{c}{c}"));
        return (Convert.ToInt32(h[..2], 16), Convert.ToInt32(h[2..4], 16), Convert.ToInt32(h[4..6], 16));
    }

    private static string Hex(double r, double g, double b)
    {
        static int C(double v) => (int)Math.Max(0, Math.Min(255, Math.Round(v, MidpointRounding.ToEven)));
        return $"#{C(r):x2}{C(g):x2}{C(b):x2}";
    }

    public static string Shade(string hex, double factor)
    {
        var (r, g, b) = Rgb(hex);
        if (factor <= 1) return Hex(r * factor, g * factor, b * factor);
        var f = factor - 1;
        return Hex(r + (255 - r) * f, g + (255 - g) * f, b + (255 - b) * f);
    }

    public static string Mix(string c1, string c2, double t)
    {
        var (r1, g1, b1) = Rgb(c1);
        var (r2, g2, b2) = Rgb(c2);
        return Hex(r1 + (r2 - r1) * t, g1 + (g2 - g1) * t, b1 + (b2 - b1) * t);
    }

    public static string BestOn(string hex)
    {
        var (r, g, b) = Rgb(hex);
        var lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
        return lum > 0.6 ? "#1f2933" : "#ffffff";
    }
}
