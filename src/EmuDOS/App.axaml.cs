using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EmuDOS.Services;
using EmuDOS.ViewModels;
using EmuDOS.Views;

namespace EmuDOS;

public partial class App : Application
{
    public AppServices Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        CrashLog.Install();            // record unhandled exceptions (incl. failures during startup below)
        UpdateService.CleanupOldFiles(); // sweep .old/.new left by a previous self-update (no-op until Phase 4/5)
        Services = new AppServices();
        Core.Audio.Mt32Synth.RegisterNativeResolver(Services.Paths.CoresDir);

        var viewModel = new MainViewModel(Services);
        var window = new MainWindow { DataContext = viewModel };
        desktop.MainWindow = window;
        window.Show();

        base.OnFrameworkInitializationCompleted();

        // Dev/smoke hook (env-gated): import a game on startup.
        var autoImport = Environment.GetEnvironmentVariable("EMUDOS_AUTOIMPORT");
        if (!string.IsNullOrWhiteSpace(autoImport))
        {
            window.Activate();
            await viewModel.ImportPathsAsync(
                autoImport.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        if (Environment.GetEnvironmentVariable("EMUDOS_AUTOPLAY") == "1")
            await window.PlayFirstAsync();

        // Check GitHub for a newer release and surface it in the bottom bar (best-effort, non-blocking).
        _ = viewModel.CheckForUpdatesAsync();

        // Backfill covers for anything already on the shelf without one.
        await viewModel.FetchMissingArtAsync();

        // Backfill descriptive metadata in the background (it's just text) so cards are pre-populated.
        _ = viewModel.FetchMissingMetadataAsync();
    }
}
