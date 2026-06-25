using System;
using System.Threading.Tasks;

namespace EmuDOS.Services;

/// <summary>A GitHub release newer than the running build.</summary>
public sealed record AppUpdate(string Tag, bool IsNewer);

/// <summary>
/// Self-update service. STUB for Phase 2 — reports no update so the shelf comes up clean. The full
/// in-place updater (tarball self-replace / .deb pkexec with SHA-256 verification, mirroring the
/// Emutastic flow) lands in Phase 4/5.
/// </summary>
public static class UpdateService
{
    public const string ReleasesUrl = "https://github.com/codingncaffeine/EmuDOS-For-Linux/releases";

    /// <summary>The running build's version (from the assembly informational version / csproj Version).</summary>
    public static string CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute { InformationalVersion: var v }, ..]
            ? v.Split('+')[0]
            : (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0");

    public static Task<AppUpdate?> LatestReleaseAsync() => Task.FromResult<AppUpdate?>(null);

    public static Task ApplyAsync(AppUpdate update, IProgress<string>? progress = null) => Task.CompletedTask;

    /// <summary>Sweep .old/.new files left by a previous self-update. No-op until the updater lands.</summary>
    public static void CleanupOldFiles() { }
}
