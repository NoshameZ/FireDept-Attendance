using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using FireDeptAttendance.Core;
using FireDeptAttendance.Services;

namespace FireDeptAttendance.Controls;

/// <summary>
/// Port of richtext.RichText: a compact WPF RichTextBox editor with B / I / U (buttons and
/// Ctrl+B / Ctrl+I / Ctrl+U), spell check with a personal dictionary, and an optional single embedded
/// signature image that can be dragged to a new spot. The editing behaviour lives in
/// <see cref="RichBoxCore"/> (shared with the page-view <see cref="PageEditor"/>).
/// Content round-trips through <see cref="RichTextModel"/> ("rich-v1" JSON).
/// </summary>
public sealed class RichTextEditor : DockPanel
{
    public RichTextBox Box { get; }
    public RichBoxCore Core { get; }
    private Button? _sigBtn;

    public RichTextEditor(double basePt, int heightLines, bool readOnly = false, bool allowSignature = false,
                          string? signaturePath = null, bool spell = true, int? minLines = null)
    {
        LastChildFill = true;
        Box = new RichTextBox
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = basePt * 96.0 / 72.0,
        };
        Box.SetResourceReference(Control.FontFamilyProperty, "AppFont");
        Box.Document.PagePadding = new Thickness(2);
        Core = new RichBoxCore(Box, readOnly, allowSignature, signaturePath);
        var lineH = Box.FontSize * 1.45;
        // Editable boxes fill the space their parent gives them and scroll inside it.
        Box.MinHeight = (minLines ?? heightLines) * lineH + 12;
        if (readOnly) Box.MaxHeight = heightLines * lineH + 12;

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
                _sigBtn = Ui.Button("Insert signature", Core.InsertSignature);
                _sigBtn.Padding = new Thickness(8, 2, 8, 2);
                bar.Children.Add(_sigBtn);
            }
            bar.Children.Add(new Separator { Width = 1, Margin = new Thickness(8, 2, 8, 2) });
            var spellChk = new CheckBox { Content = "Spell check", IsChecked = spell };
            spellChk.Checked += (_, _) => Core.SetSpell(true);
            spellChk.Unchecked += (_, _) => Core.SetSpell(false);
            bar.Children.Add(spellChk);
            SetDock(bar, Dock.Top);
            Children.Add(bar);
            Core.SetSpell(spell);
        }
        Children.Add(Box);
        Core.SignatureChanged += UpdateSigButton;
        UpdateSigButton();
    }

    private Button FmtButton(string label, RoutedUICommand cmd, Action<Button> style)
    {
        var b = new Button { Content = label, Width = 30, Padding = new Thickness(0, 2, 0, 2), Margin = new Thickness(0, 0, 4, 0), Focusable = false };
        style(b);
        b.Click += (_, _) => Core.Execute(cmd);
        return b;
    }

    private void UpdateSigButton()
    {
        if (_sigBtn != null) _sigBtn.IsEnabled = Core.HasUsableSignatureFile;
    }

    public void SetContent(string? stored) => Core.SetContent(stored);
    public string GetContent() => Core.GetContent();
    public List<List<RichRun>> GetParagraphs() => Core.GetParagraphs();
    public bool IsEmpty => Core.IsEmpty;
    public void SetSignaturePath(string? path) => Core.SetSignaturePath(path);
    public bool HasSignatureImage => Core.HasSignatureImage;
    public void RemoveSignature() => Core.RemoveSignature();
}
