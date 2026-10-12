using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FireDeptAttendance.Core;
using FireDeptAttendance.Services;

namespace FireDeptAttendance.Dialogs;

/// <summary>Modal dialog to add a new member (structured name + status).</summary>
public sealed class AddMemberDialog : Window
{
    private readonly TextBox _first = new() { Width = 220 };
    private readonly TextBox _mi = new() { Width = 50, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _last = new() { Width = 220 };
    private readonly TextBox _nick = new() { Width = 220 };
    private readonly TextBox _radio = new() { Width = 220 };
    private readonly ComboBox _status = new() { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };

    public long? NewId { get; private set; }

    public AddMemberDialog(Window? owner)
    {
        Ui.SetupDialog(this, "Add New Member", owner, 420, 380, resizable: false);
        SizeToContent = SizeToContent.WidthAndHeight;

        var g = Ui.FormGrid();
        g.Margin = new Thickness(16);
        var header = Ui.Text("Add New Member");
        header.FontSize = 16; header.FontWeight = FontWeights.Bold;
        header.Margin = new Thickness(0, 0, 0, 10);
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumnSpan(header, 2);
        g.Children.Add(header);

        _status.Items.Add("Active");
        _status.Items.Add("Inactive");
        _status.SelectedIndex = 0;

        Ui.AddRow(g, 1, "First name *:", _first);
        Ui.AddRow(g, 2, "Middle initial:", _mi);
        Ui.AddRow(g, 3, "Last name *:", _last);
        Ui.AddRow(g, 4, "Nickname:", _nick);
        Ui.AddRow(g, 5, "Radio number:", _radio);
        Ui.AddRow(g, 6, "Status:", _status);
        foreach (var tb in new[] { _first, _mi, _last, _nick, _radio })
        {
            tb.HorizontalAlignment = HorizontalAlignment.Left;
            tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Save(); } };
        }

        var note = Ui.Text("Active members count toward the 40-member limit; inactive members do not.", Lbl.Sub, true);
        note.MaxWidth = 320; note.HorizontalAlignment = HorizontalAlignment.Left;
        note.Margin = new Thickness(0, 6, 0, 0);
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(note, 7); Grid.SetColumnSpan(note, 2);
        g.Children.Add(note);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        btns.Children.Add(Ui.M(Ui.Button("Add", Save, "AccentButton"), 4, 0, 4, 0));
        btns.Children.Add(Ui.M(Ui.Button("Cancel", Close), 4, 0, 4, 0));
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(btns, 8); Grid.SetColumnSpan(btns, 2);
        g.Children.Add(btns);

        Content = g;
        Loaded += (_, _) => _first.Focus();
    }

    private void Save()
    {
        var first = _first.Text.Trim();
        var last = _last.Text.Trim();
        if (first.Length == 0 || last.Length == 0)
        {
            Ui.Warn("Add member", "First name and last name are both required.");
            return;
        }
        try
        {
            NewId = App.Db.AddMember(first, last, _mi.Text.Trim(), _nick.Text.Trim(),
                                     active: (string)_status.SelectedItem == "Active", radio: _radio.Text.Trim());
        }
        catch (ValidationException ex)
        {
            Ui.Error("Cannot add member", ex.Message);
            return;
        }
        Close();
    }
}
