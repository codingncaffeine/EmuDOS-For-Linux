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

    public static Task<AppUpdate?> LatestReleaseAsync() => Task.FromResult<AppUpdate?>(null);

    public static Task ApplyAsync(AppUpdate update, IProgress<string>? progress = null) => Task.CompletedTask;

    /// <summary>Sweep .old/.new files left by a previous self-update. No-op until the updater lands.</summary>
    public static void CleanupOldFiles() { }
}
