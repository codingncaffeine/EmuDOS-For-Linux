using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EmuDOS.Services;

/// <summary>
/// A GitHub release. <see cref="IsNewer"/> is true when it's strictly newer than the running build;
/// <see cref="Asset"/>/<see cref="Kind"/> carry what <see cref="UpdateService.ApplyAsync"/> needs.
/// </summary>
public sealed record AppUpdate(
    string Tag,
    bool IsNewer,
    UpdateService.ReleaseAsset? Asset = null,
    UpdateService.InstallKind Kind = UpdateService.InstallKind.Dev);

/// <summary>
/// In-app self-updater (Linux). Consumes the GitHub release artifacts that packaging/build-release.sh
/// produces — the asset names are a contract:
///   EmuDOS-&lt;ver&gt;-linux-x64.tar.gz   self-contained tarball
///   emudos_&lt;ver&gt;_amd64.deb           system package
///
/// Apply strategy depends on how THIS copy is installed:
///   SelfContained — exe dir is user-writable (tarball extract): download tarball → extract to
///       .update-staging → spawn a detached script that waits for our exit, copies staging over the
///       install (replacing the running binary only once we're gone — avoids ETXTBSY), and relaunches.
///   Deb — exe lives under /usr/: download the .deb → `pkexec dpkg -i` (GUI auth) → relaunch.
///   Dev — running from a build tree (bin/Release|Debug): the About tab says "update via git".
///   ReadOnly — unwritable non-/usr dir: fall back to the releases page.
///
/// EMUDOS_UPDATE_API overrides the releases/latest endpoint for integration tests.
/// </summary>
public static class UpdateService
{
    public const string ReleasesUrl = "https://github.com/codingncaffeine/EmuDOS-For-Linux/releases";

    private const string DefaultLatestApi =
        "https://api.github.com/repos/codingncaffeine/EmuDOS-For-Linux/releases/latest";

    private static string LatestApi =>
        Environment.GetEnvironmentVariable("EMUDOS_UPDATE_API") ?? DefaultLatestApi;

    public enum InstallKind { Dev, Deb, SelfContained, ReadOnly }

    public sealed record ReleaseAsset(string Name, string Url, long Size, string? Digest = null);

    /// <summary>The running build's version (from the assembly informational version / csproj Version).</summary>
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            is [AssemblyInformationalVersionAttribute { InformationalVersion: var v }, ..]
            ? v.Split('+')[0]
            : (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0");

    private static string ExeFolder => AppContext.BaseDirectory.TrimEnd('/');

    public static InstallKind DetectInstallKind()
    {
        var dir = ExeFolder.Replace('\\', '/');
        if (dir.Contains("/bin/Release/") || dir.Contains("/bin/Debug/")
            || dir.EndsWith("/bin/Release") || dir.EndsWith("/bin/Debug"))
            return InstallKind.Dev;
        if (dir.StartsWith("/usr/"))
            return InstallKind.Deb;
        try
        {
            var probe = Path.Combine(ExeFolder, ".write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return InstallKind.SelfContained;
        }
        catch { return InstallKind.ReadOnly; }
    }

    /// <summary>The latest GitHub release with a self-update verdict, or null if it couldn't be checked
    /// (offline / API error). IsNewer is false when already up to date.</summary>
    public static async Task<AppUpdate?> LatestReleaseAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EmuDOS/updater");
            var json = await http.GetStringAsync(LatestApi).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (!Version.TryParse(tag.TrimStart('v', 'V').Trim(), out var remote))
                return null;

            bool isNewer = !Version.TryParse(CurrentVersion.Split('+')[0], out var local)
                ? false
                : Norm(remote).CompareTo(Norm(local)) > 0;

            var kind = DetectInstallKind();
            ReleaseAsset? asset = null;
            if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var list = new System.Collections.Generic.List<ReleaseAsset>();
                foreach (var a in arr.EnumerateArray())
                    list.Add(new ReleaseAsset(
                        a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "",
                        a.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                        a.TryGetProperty("digest", out var d) ? d.GetString() : null));
                asset = PickAsset(kind, list);
            }

            return new AppUpdate(tag, isNewer, asset, kind);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Update] check failed: {ex.Message}");
            return null;
        }

        static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>Picks the asset matching this install kind, or null.</summary>
    public static ReleaseAsset? PickAsset(InstallKind kind, System.Collections.Generic.IReadOnlyList<ReleaseAsset> assets)
    {
        foreach (var a in assets)
        {
            bool deb = a.Name.StartsWith("emudos_", StringComparison.OrdinalIgnoreCase)
                       && a.Name.EndsWith("_amd64.deb", StringComparison.OrdinalIgnoreCase);
            bool tar = a.Name.StartsWith("EmuDOS-", StringComparison.OrdinalIgnoreCase)
                       && a.Name.EndsWith("-linux-x64.tar.gz", StringComparison.OrdinalIgnoreCase);
            if (kind == InstallKind.Deb && deb) return a;
            if (kind == InstallKind.SelfContained && tar) return a;
        }
        return null;
    }

    /// <summary>Downloads the release for <paramref name="update"/>, verifies it, applies it, and exits
    /// so the relaunch script can swap files. Throws with a user-facing message on any failure.</summary>
    public static async Task ApplyAsync(AppUpdate update, IProgress<string>? progress = null)
    {
        if (update.Kind is InstallKind.Dev)
            throw new InvalidOperationException("This is a development build — update with git, not the in-app updater.");
        if (update.Kind is InstallKind.ReadOnly)
            throw new InvalidOperationException("This install location isn't writable — update from the releases page or your package manager.");
        if (update.Asset is not { } asset || string.IsNullOrEmpty(asset.Url))
            throw new InvalidOperationException($"No {(update.Kind == InstallKind.Deb ? ".deb" : "tarball")} asset was found for this release.");

        var ct = CancellationToken.None;
        var tmp = Path.Combine(Path.GetTempPath(), $"emudos-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var file = Path.Combine(tmp, asset.Name);

        progress?.Report("Downloading update…");
        using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EmuDOS/updater");
            using var resp = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? asset.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(file);
            var buf = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                    progress?.Report($"Downloading… {done / 1048576} / {total / 1048576} MB");
            }
        }

        // Integrity gate: verify against GitHub's published SHA-256 BEFORE we extract over our own
        // binary or hand it to pkexec dpkg. A mismatch means corruption/tampering — abort.
        if (!string.IsNullOrEmpty(asset.Digest))
        {
            progress?.Report("Verifying…");
            var expected = asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? asset.Digest[7..] : asset.Digest;
            var actual = await Sha256HexAsync(file, ct);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Update integrity check failed — the download didn't match the expected checksum, "
                    + "so nothing was installed. Try again, or update from the releases page.");
        }

        if (update.Kind == InstallKind.SelfContained)
            await ApplyTarballAsync(file, progress, ct);
        else
            await ApplyDebAsync(file, progress, ct);
    }

    private static async Task ApplyTarballAsync(string tarball, IProgress<string>? progress, CancellationToken ct)
    {
        var install = ExeFolder;
        var staging = Path.Combine(install, ".update-staging");
        progress?.Report("Extracting…");
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        var tar = Process.Start(new ProcessStartInfo("tar", $"-xzf \"{tarball}\" -C \"{staging}\"")
        { UseShellExecute = false, RedirectStandardError = true })
            ?? throw new InvalidOperationException("Couldn't start tar.");
        await tar.WaitForExitAsync(ct);
        if (tar.ExitCode != 0)
            throw new InvalidOperationException("Archive extraction failed.");

        // The tarball may nest the payload in a top-level folder; find the dir that holds the EmuDOS binary.
        var payload = File.Exists(Path.Combine(staging, "EmuDOS"))
            ? staging
            : System.Linq.Enumerable.FirstOrDefault(Directory.EnumerateDirectories(staging),
                  d => File.Exists(Path.Combine(d, "EmuDOS")))
              ?? throw new InvalidOperationException("Archive doesn't look like an EmuDOS release.");

        var script = Path.Combine(Path.GetTempPath(), $"emudos-apply-{Environment.ProcessId}.sh");
        await File.WriteAllTextAsync(script, $"""
            #!/bin/sh
            # EmuDOS self-update: wait for the app to exit, swap files, relaunch.
            tail --pid={Environment.ProcessId} -f /dev/null
            cp -a "{payload}/." "{install}/"
            rm -rf "{staging}"
            rm -f "{tarball}"
            exec "{install}/EmuDOS"
            """, ct);
        Process.Start(new ProcessStartInfo("setsid", $"bash \"{script}\"") { UseShellExecute = false });

        progress?.Report("Restarting…");
        await Task.Delay(400, ct);
        Environment.Exit(0);
    }

    private static async Task ApplyDebAsync(string deb, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Waiting for authorization…");
        var psi = new ProcessStartInfo("pkexec", $"dpkg -i \"{deb}\"") { UseShellExecute = false };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start pkexec.");
        await p.WaitForExitAsync(ct);
        if (p.ExitCode is 126 or 127)
            throw new InvalidOperationException("Authorization was cancelled.");
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Package install failed (dpkg exit {p.ExitCode}).");

        var script = Path.Combine(Path.GetTempPath(), $"emudos-apply-{Environment.ProcessId}.sh");
        await File.WriteAllTextAsync(script, $"""
            #!/bin/sh
            tail --pid={Environment.ProcessId} -f /dev/null
            rm -f "{deb}"
            exec /usr/lib/emudos/EmuDOS
            """, ct);
        Process.Start(new ProcessStartInfo("setsid", $"bash \"{script}\"") { UseShellExecute = false });

        progress?.Report("Restarting…");
        await Task.Delay(400, ct);
        Environment.Exit(0);
    }

    private static async Task<string> Sha256HexAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Sweep a leftover .update-staging from an interrupted update. Safe to call at startup.</summary>
    public static void CleanupOldFiles()
    {
        try
        {
            var staging = Path.Combine(ExeFolder, ".update-staging");
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
        catch { /* best effort */ }
    }
}
