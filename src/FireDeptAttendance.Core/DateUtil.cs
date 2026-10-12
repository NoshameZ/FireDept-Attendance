using System.Globalization;

namespace FireDeptAttendance.Core;

/// <summary>
/// Date helpers. Dates are stored as ISO <c>yyyy-MM-dd</c> and shown/typed as
/// US <c>MM/DD/YYYY</c> (port of <c>datewidgets.py</c>).
/// </summary>
public static class DateUtil
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Python accepted %m/%d/%Y, %m-%d-%Y, %m/%d/%y (strptime allows 1-digit month/day).
    private static readonly string[] InputFormats =
    {
        "M/d/yyyy", "MM/dd/yyyy", "M-d-yyyy", "MM-dd-yyyy", "M/d/yy", "MM/dd/yy",
    };

    public static string Today() => DateTime.Today.ToString("yyyy-MM-dd", Inv);

    /// <summary>ISO → MM/DD/YYYY. Blank → "". Unparseable values are returned unchanged.</summary>
    public static string IsoToUs(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        iso = iso.Trim();
        return DateTime.TryParseExact(iso, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d)
            ? d.ToString("MM/dd/yyyy", Inv) : iso;
    }

    /// <summary>User-typed MM/DD/YYYY → ISO. Blank → "". Throws <see cref="FormatException"/> if invalid.</summary>
    public static string UsToIso(string? text)
    {
        if (text is null) return "";
        text = text.Trim();
        if (text.Length == 0) return "";
        if (DateTime.TryParseExact(text, InputFormats, Inv, DateTimeStyles.None, out var d))
            return d.ToString("yyyy-MM-dd", Inv);
        throw new FormatException($"'{text}' is not a valid date. Use MM/DD/YYYY (for example, 03/09/2025).");
    }

    public static bool TryUsToIso(string? text, out string iso)
    {
        try { iso = UsToIso(text); return iso.Length > 0; }
        catch (FormatException) { iso = ""; return false; }
    }

    public static DateTime? ParseIso(string? iso) =>
        DateTime.TryParseExact((iso ?? "").Trim(), "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Calendar-accurate (years, months, days) between join and end (or today).</summary>
    public static (int Years, int Months, int Days)? ServiceDuration(string? joinIso, string? endIso = null)
    {
        if (string.IsNullOrEmpty(joinIso)) return null;
        var start = ParseIso(joinIso);
        if (start is null) return null;
        var end = (!string.IsNullOrEmpty(endIso) ? ParseIso(endIso) : null) ?? DateTime.Today;
        if (end < start) return (0, 0, 0);
        int years = end.Year - start.Value.Year;
        int months = end.Month - start.Value.Month;
        int days = end.Day - start.Value.Day;
        if (days < 0)
        {
            months -= 1;
            var pm = end.AddMonths(-1);
            days += DateTime.DaysInMonth(pm.Year, pm.Month);
        }
        if (months < 0) { years -= 1; months += 12; }
        return (years, months, days);
    }

    public static string FormatServiceTime(string? joinIso, string? endIso = null)
    {
        var d = ServiceDuration(joinIso, endIso);
        if (d is null) return "";
        static string Plural(int n, string w) => $"{n} {w}" + (n == 1 ? "" : "s");
        return string.Join(", ", Plural(d.Value.Years, "year"), Plural(d.Value.Months, "month"),
            Plural(d.Value.Days, "day"));
    }

    public static readonly (int Years, string Emblem)[] ServiceMilestones =
    {
        (5, "\u2b50"),
        (10, "\u2b50\u2b50"),
        (15, "\u2b50\u2b50\u2b50"),
        (20, "\u2b50\u2b50\u2b50\u2b50"),
        (25, "\U0001f3c5"),
        (30, "\U0001f3c5\u2b50"),
        (35, "\U0001f3c5\u2b50\u2b50"),
        (40, "\U0001f3c5\u2b50\u2b50\u2b50"),
    };

    /// <summary>(emblem, milestoneYears) for the highest 5-year milestone reached, or ("", 0).</summary>
    public static (string Emblem, int Years) MilestoneEmblem(string? joinIso, string? endIso = null)
    {
        var d = ServiceDuration(joinIso, endIso);
        if (d is null) return ("", 0);
        (string, int) best = ("", 0);
        foreach (var (t, s) in ServiceMilestones)
        {
            if (d.Value.Years >= t) best = (s, t); else break;
        }
        return best;
    }

    public static string QuarterLabel(int q) => q switch
    {
        1 => "Q1 (Jan-Mar)",
        2 => "Q2 (Apr-Jun)",
        3 => "Q3 (Jul-Sep)",
        _ => "Q4 (Oct-Dec)",
    };

    public static int CurrentQuarter() => Database.QuarterForMonth(DateTime.Today.Month);
}
