using System;
using System.IO;
using System.Threading.Tasks;

namespace EmuDOS.Services;

/// <summary>Records unhandled exceptions (including failures during startup) to a crash log in the
/// data folder, so a crash leaves a trace even with no console attached.</summary>
public static class CrashLog
{
    private static string? _logPath;

    public static void Install()
    {
        try { _logPath = Path.Combine(new EmuDOS.Core.Infrastructure.AppPaths().DataRoot, "crash.log"); }
        catch { /* fall back to no file */ }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, e) => { Write(e.Exception, "Task"); e.SetObserved(); };
    }

    private static void Write(Exception? ex, string source)
    {
        if (ex is null) return;
        var line = $"[{DateTime.Now:O}] {source}: {ex}\n";
        try { Console.Error.Write(line); } catch { }
        if (_logPath is null) return;
        try { File.AppendAllText(_logPath, line); } catch { /* best effort */ }
    }
}
