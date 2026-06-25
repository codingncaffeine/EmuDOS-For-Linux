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
        UpdateService.CleanupOldFiles(); // sweep a leftover .update-staging from an interrupted self-update
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
