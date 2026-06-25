using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EmuDOS.Views;

/// <summary>A small themed yes/no confirmation dialog (replaces the Win32 MessageBox).</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog() => InitializeComponent();

    /// <summary>Show the dialog modally; true if the user confirmed. (Avalonia's ShowDialog is async.)</summary>
    public static Task<bool> ShowAsync(Window owner, string title, string message, string confirmText = "OK")
    {
        var dialog = new ConfirmDialog();
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmBtn.Content = confirmText;
        return dialog.ShowDialog<bool>(owner);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
}
