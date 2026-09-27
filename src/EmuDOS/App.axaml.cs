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

    private async Task RefreshCatalogAsync()
    {
        try
        {
            await Services.CatalogReady;
            var result = await Services.CatalogUpdater.UpdateAsync();
            if (result.Installed)
                Services.SystemLog.Info($"Catalog: updated to revision {result.Revision} ({result.Entries} games).");
            else if (result.Error is not null)
                Services.SystemLog.Info($"Catalog: update check failed: {result.Error}");
            else
                Services.SystemLog.Info($"Catalog: up to date (revision {result.Revision}, {result.Entries} games).");
        }
        catch (Exception ex)
        {
            Services.SystemLog.Error($"Catalog: update failed: {ex.Message}");
        }
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        CrashLog.Install();            // record unhandled exceptions (incl. failures during startup below)
        UpdateService.CleanupOldFiles(); // sweep a leftover .update-staging from an interrupted self-update
        Services = new AppServices();
        Core.Audio.Mt32Synth.RegisterNativeResolver(Services.Paths.CoresDir);
        // Game audio's [DllImport("SDL3")] resolves through the same loader as the gamepads: the bundled
        // libSDL3.so.0 first, then the system's versioned soname (a plain import only finds libSDL3.so).
        Core.Input.Sdl3Library.ResolveImportsFor(typeof(App).Assembly, Services.Paths.CoresDir);
        _ = Task.Run(() => Services.SystemLog.Info(Core.Input.Sdl3Library.Load(Services.Paths.CoresDir) != IntPtr.Zero
            ? $"SDL3: loaded {Core.Input.Sdl3Library.LoadedFrom} (audio import {(Platform.SdlAudio.ImportResolves() ? "ok" : "FAILED")})"
            : "SDL3: not found — games will have no sound and gamepads won't work"));

        var viewModel = new MainViewModel(Services);
        var window = new MainWindow { DataContext = viewModel };
        desktop.MainWindow = window;
        window.Show();

        base.OnFrameworkInitializationCompleted();

        // Refresh the curated catalog from the latest release (best-effort, off the UI thread). Same
        // consent as the update check: both only contact GitHub when it is enabled.
        if (Services.Settings.CheckForUpdates)
            _ = Task.Run(RefreshCatalogAsync);

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
        if (Environment.GetEnvironmentVariable("EMUDOS_AUTOCARD") == "1")
            window.OpenFirstCard();
        if (Environment.GetEnvironmentVariable("EMUDOS_AUTOPREFS") == "1")
            window.OpenPreferencesForSmoke();
        if (Environment.GetEnvironmentVariable("EMUDOS_AUTOCHEAT") == "1")
            new Views.CheatWindow().Show();
        if (Environment.GetEnvironmentVariable("EMUDOS_AUTOLCD") == "1")
        {
            var lcd = new Views.Mt32LcdWindow();
            lcd.Show();
            lcd.SetText("EMUDOS  MT-32");
        }
        if (Environment.GetEnvironmentVariable("EMUDOS_SHADERTEST") == "1")
        {
            var gl = Effects.Egl.GlDevice.TryCreate();
            Console.WriteLine($"SHADERTEST: GlDevice={(gl is null ? "null" : "OK (EGL+GL context up)")}");
            gl?.Dispose();
            var r = new Effects.Librashader.ShaderRenderer();
            bool ok = r.Initialize(Services.Paths.LibrashaderDllPath, "/nonexistent.slangp");
            Console.WriteLine($"SHADERTEST: ShaderRenderer.Initialize={ok}, LastError={r.LastError}");
            r.Dispose();
        }

        // Check GitHub for a newer release and surface it in the bottom bar (best-effort, non-blocking).
        _ = viewModel.CheckForUpdatesAsync();

        // Backfill covers for anything already on the shelf without one.
        await viewModel.FetchMissingArtAsync();

        // Backfill descriptive metadata in the background (it's just text) so cards are pre-populated.
        _ = viewModel.FetchMissingMetadataAsync();
    }
}
