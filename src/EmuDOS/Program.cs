using Avalonia;
using System;

namespace EmuDOS;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized yet.
    [STAThread]
    public static void Main(string[] args)
    {
        // Headless host validation (no Avalonia/window): EmuDOS --selftest-core <core.so>.
        if (args is ["--selftest-core", var corePath, ..])
        {
            Environment.Exit(EmuDOS.Services.CoreSelfTest.Run(corePath));
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    // X11 + Skia explicitly (Linux target). We don't reference Avalonia.Desktop — see the
    // vendored-Avalonia note in the csproj — so UsePlatformDetect() isn't available here.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseX11()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .LogToTrace();
}
