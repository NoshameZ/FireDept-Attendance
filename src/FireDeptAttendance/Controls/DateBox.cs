using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FireDeptAttendance.Core;

namespace FireDeptAttendance.Controls;

/// <summary>
/// Port of datewidgets.DateField: a typed MM/DD/YYYY text box (no calendar popup).
/// <see cref="Committed"/> fires once the user finishes typing (Enter or focus loss),
/// deferred through the dispatcher so heavy work never runs inside the focus handler,
/// and only when the text actually changed since the last commit.
/// </summary>
public sealed class DateBox : TextBox
{
    private string _lastCommitted = "";

    public event Action? Committed;

    public DateBox(string iso = "")
    {
        Width = 110;
        HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip = "MM/DD/YYYY";
        SetIso(iso);
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return) { e.Handled = true; FireCommit(); }
        };
        LostKeyboardFocus += (_, _) => FireCommit();
    }

    private void FireCommit()
    {
        var now = Text.Trim();
        if (now == _lastCommitted) return;
        _lastCommitted = now;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try { Committed?.Invoke(); } catch { /* never break typing */ }
        });
    }

    public string UsText => Text.Trim();

    /// <summary>ISO date, throwing FormatException for malformed input ("" for blank).</summary>
    public string GetIso() => DateUtil.UsToIso(Text);

    public void SetIso(string? iso)
    {
        Text = DateUtil.IsoToUs(iso);
        _lastCommitted = Text.Trim();
    }

    public void SetUs(string? text)
    {
        Text = text ?? "";
        _lastCommitted = Text.Trim();
    }
}
