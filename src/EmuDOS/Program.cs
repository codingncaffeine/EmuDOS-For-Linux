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
        // Build the curated catalog from its (local, readable) source list: EmuDOS --build-catalog
        // <source.json> <out.db>. Refuses a result in which any source name is readable.
        if (args is ["--build-catalog", var catalogSource, var catalogOut, ..])
        {
            var report = EmuDOS.Core.Catalog.CatalogSource.Build(catalogSource, catalogOut);
            Console.WriteLine($"catalog: {report.Entries} entries, revision {report.Revision} -> {catalogOut}");
            foreach (var leak in report.Leaks)
                Console.Error.WriteLine($"catalog: READABLE NAME in output: {leak}");
            Environment.Exit(report.Leaks.Count == 0 ? 0 : 3);
            return;
        }

        // Import trial boot (no Avalonia/window): EmuDOS --trial-boot <core.so> <content> <program>.
        if (args is ["--trial-boot", var trialCore, var trialContent, var trialProgram, ..])
        {
            Environment.Exit(EmuDOS.Services.TrialBootHost.Run(trialCore, trialContent, trialProgram));
            return;
        }

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
