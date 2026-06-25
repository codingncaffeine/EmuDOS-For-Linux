using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EmuDOS.Views;

/// <summary>A clickable picker of the programs found in a game's content — the game exe, an installer,
/// a setup tool — so the user can choose what to launch instead of dropping to DOS. The async
/// <see cref="ShowAsync"/> returns the chosen executable path, or null if cancelled.</summary>
public partial class ChooseProgramDialog : Window
{
    public ChooseProgramDialog() => InitializeComponent();

    public static Task<string?> ShowAsync(Window owner, string title, IEnumerable<string> executables, string? current)
    {
        var d = new ChooseProgramDialog();
        d.TitleText.Text = $"Run a program — {title}";

        var items = executables.Select(exe => new ExeItem(exe, IsSetupLike(exe))).ToList();
        d.ExeList.ItemsSource = items;
        d.ExeList.SelectedItem =
            (current is not null ? items.FirstOrDefault(i => string.Equals(i.Path, current, StringComparison.OrdinalIgnoreCase)) : null)
            ?? items.FirstOrDefault();

        return d.ShowDialog<string?>(owner);
    }

    private void OnListDoubleTapped(object? sender, RoutedEventArgs e) => Run();
    private void OnRunClick(object? sender, RoutedEventArgs e) => Run();
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void Run()
    {
        if (ExeList.SelectedItem is ExeItem item)
            Close(item.Path);
    }

    private static bool IsSetupLike(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        return name.Contains("setup") || name.Contains("install") || name.Contains("config");
    }

    private sealed record ExeItem(string Path, bool IsSetup)
    {
        public override string ToString() => IsSetup ? $"{Path}   —  setup tool" : Path;
    }
}
