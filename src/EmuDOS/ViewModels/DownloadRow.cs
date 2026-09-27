using System;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EmuDOS.Core.Downloads;

namespace EmuDOS.ViewModels;

/// <summary>A row in the Preferences → Downloads list: a fetchable component (the core, catalog, or a
/// custom download like the CRT shaders) with live status while it downloads.</summary>
public sealed partial class DownloadRow : ObservableObject
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x90));
    private static readonly IBrush Success = new SolidColorBrush(Color.FromRgb(0x9F, 0xE0, 0xA0));
    private static readonly IBrush Failure = new SolidColorBrush(Color.FromRgb(0xE0, 0x85, 0x85));

    public string Name { get; }
    public string Description { get; }
    public DownloadAsset? Asset { get; }
    /// <summary>A non-standard download (e.g. the slang shader pack), reporting progress as text.</summary>
    public Func<Action<string>, Task>? CustomDownload { get; }

    /// <summary>Computes the "installed" status line (e.g. the catalog's game count) off the UI thread;
    /// null keeps the plain "Installed.".</summary>
    public Func<Task<string>>? InstalledStatus { get; init; }

    /// <summary>The button caption once installed (default "Re-download").</summary>
    public string InstalledAction { get; init; } = "Re-download";

    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private IBrush _statusBrush = Muted;

    public DownloadRow(DownloadAsset asset, bool installed)
    {
        Asset = asset;
        Name = asset.DisplayName;
        Description = asset.Description;
        _isInstalled = installed;
        _status = installed ? "Installed." : "Not installed.";
        _statusBrush = installed ? Success : Muted;
    }

    public DownloadRow(string name, string description, bool installed, Func<Action<string>, Task> customDownload)
    {
        Name = name;
        Description = description;
        CustomDownload = customDownload;
        _isInstalled = installed;
        _status = installed ? "Installed." : "Not installed.";
        _statusBrush = installed ? Success : Muted;
    }

    // ActionText / CanDownload depend on IsBusy + IsInstalled, so re-raise them when either changes.
    public string ActionText => IsBusy ? "Working…" : IsInstalled ? InstalledAction : "Download";
    public bool CanDownload => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(CanDownload));
    }

    partial void OnIsInstalledChanged(bool value) => OnPropertyChanged(nameof(ActionText));

    public void SetProgress(string message)
    {
        Status = message;
        StatusBrush = Muted;
    }

    public void SetResult(bool ok, string? error)
    {
        IsInstalled = ok || IsInstalled;
        Status = ok ? "Installed." : $"Failed: {error}";
        StatusBrush = ok ? Success : Failure;
    }

    /// <summary>Replace "Installed." with <see cref="InstalledStatus"/> when the row has one.</summary>
    public async Task RefreshInstalledStatusAsync()
    {
        if (InstalledStatus is null || !IsInstalled)
            return;
        try
        {
            Status = await InstalledStatus();
            StatusBrush = Success;
        }
        catch (Exception ex)
        {
            Status = $"Unavailable: {ex.Message}";
            StatusBrush = Failure;
        }
    }
}
