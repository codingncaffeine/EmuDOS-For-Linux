using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmuDOS.Core.Import;

namespace EmuDOS.Services;

/// <summary>
/// Runs each trial boot in a child EmuDOS process (<see cref="TrialBootHost"/>), so the emulator
/// core never runs twice in one process and a program that hangs the core only costs the timeout.
/// Unavailable when the core has not been downloaded yet.
/// </summary>
public sealed class ProcessTrialBooter(Func<string?> corePath) : ITrialBooter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<TrialOutcome> BootAsync(string contentDir, string executable, CancellationToken cancellationToken = default)
    {
        var core = corePath();
        var self = Environment.ProcessPath;
        if (core is null || !File.Exists(core) || self is null)
            return TrialOutcome.Unavailable;

        var psi = new ProcessStartInfo(self) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        // Under `dotnet EmuDOS.dll` the process is the dotnet host; hand it the app assembly first.
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "EmuDOS.dll"));
        psi.ArgumentList.Add("--trial-boot");
        psi.ArgumentList.Add(core);
        psi.ArgumentList.Add(contentDir);
        psi.ArgumentList.Add(executable);

        using var process = Process.Start(psi);
        if (process is null)
            return TrialOutcome.Unavailable;
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return TrialOutcome.Unavailable;
        }
        return process.ExitCode switch
        {
            TrialBootHost.ExitStillRunning => TrialOutcome.StillRunning,
            TrialBootHost.ExitReturnedToDos => TrialOutcome.ReturnedToDos,
            _ => TrialOutcome.Unavailable,
        };
    }
}
