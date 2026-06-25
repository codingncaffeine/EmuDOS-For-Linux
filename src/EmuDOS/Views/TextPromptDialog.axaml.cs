using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace EmuDOS.Views;

/// <summary>A minimal single-line text prompt (title, description, input + Save/Cancel). The async
/// <see cref="ShowAsync"/> returns the trimmed text, or null if cancelled.</summary>
public partial class TextPromptDialog : Window
{
    public TextPromptDialog() => InitializeComponent();

    public static Task<string?> ShowAsync(Window owner, string title, string description, string? current)
    {
        var d = new TextPromptDialog();
        d.TitleText.Text = title;
        d.DescText.Text = description;
        d.InputBox.Text = current ?? string.Empty;
        d.Opened += (_, _) => { d.InputBox.Focus(); d.InputBox.SelectAll(); };
        return d.ShowDialog<string?>(owner);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Accept();
        else if (e.Key == Key.Escape) Close(null);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void Accept() => Close(InputBox.Text?.Trim() ?? string.Empty);
}
