using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FireDeptAttendance.Controls;

/// <summary>Helpers for the read-only, header-sortable lists (Treeview equivalents).</summary>
public static class GridHelper
{
    public static DataGridTextColumn Col(string header, string path, double width, string? sortKey,
                                         bool center = true, bool star = false)
    {
        var tbStyle = new Style(typeof(TextBlock));
        tbStyle.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty,
            center ? HorizontalAlignment.Center : HorizontalAlignment.Left));
        tbStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        return new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path),
            Width = star ? new DataGridLength(width, DataGridLengthUnitType.Star) : new DataGridLength(width),
            MinWidth = 30,
            SortMemberPath = sortKey ?? "",
            CanUserSort = sortKey != null,
            ElementStyle = tbStyle,
        };
    }

    /// <summary>Show " ▲" / " ▼" on the active sort column, plain titles elsewhere.</summary>
    public static void UpdateArrows(DataGrid g, IReadOnlyDictionary<string, string> titles, string col, bool reverse)
    {
        foreach (var c in g.Columns)
        {
            if (string.IsNullOrEmpty(c.SortMemberPath) || !titles.TryGetValue(c.SortMemberPath, out var t)) continue;
            c.Header = c.SortMemberPath == col ? t + (reverse ? " ▼" : " ▲") : t;
            c.SortDirection = null;
        }
    }

    /// <summary>Wire header clicks to a custom sort callback (same column again flips direction).</summary>
    public static void HookSorting(DataGrid g, Func<(string Col, bool Rev)> get, Action<string, bool> set)
    {
        g.Sorting += (_, e) =>
        {
            e.Handled = true;
            var key = e.Column.SortMemberPath;
            if (string.IsNullOrEmpty(key)) return;
            var (col, rev) = get();
            set(key, col == key ? !rev : false);
        };
    }

    public static void ScrollTo(DataGrid g, object? item)
    {
        if (item == null) return;
        try { g.ScrollIntoView(item); } catch { /* not yet laid out */ }
    }
}
