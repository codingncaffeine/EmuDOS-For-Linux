using System.IO.Compression;
using System.Security.Cryptography;
using EmuDOS.Core.Infrastructure;

namespace EmuDOS.Core.Downloads;

/// <summary>
/// Streams a download to disk with progress, extracts a zipped core's DLL, and records the
/// installed file's SHA-256 (so a later updater can hash-check before trusting/replacing it
/// — a hard Emutastic lesson). The <see cref="HttpClient"/> is injected for testability.
/// </summary>
public sealed class DownloadService(HttpClient http, AppPaths paths) : IDownloadService
{
    private const int BufferSize = 81920;

    public bool IsInstalled(DownloadAsset asset) => File.Exists(InstalledPath(asset));

    public string InstalledPath(DownloadAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var dir = asset.Category switch
        {
            AssetCategory.Core or AssetCategory.Native => paths.CoresDir,
            AssetCategory.SoundFont or AssetCategory.Bios => paths.SystemDir,
            AssetCategory.Catalog => paths.CatalogDir,
            _ => paths.DataRoot,
        };
        return Path.Combine(dir, asset.FileName);
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadAsset asset,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var tempFile = Path.Combine(Path.GetTempPath(), $"emudos-dl-{Guid.NewGuid():N}.tmp");
        try
        {
            await DownloadToFileAsync(asset.Url, tempFile, progress, cancellationToken);

            var finalPath = InstalledPath(asset);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            if (asset.Kind == DownloadKind.ZippedCore)
                ExtractEntry(tempFile, asset.FileName, finalPath);
            else if (asset.Kind == DownloadKind.TarXzBinary)
                ExtractTarXzBinary(tempFile, asset.FileName, finalPath);
            else
                File.Copy(tempFile, finalPath, overwrite: true);

            var sha = ComputeSha256(finalPath);
            if (asset.Sha256 is not null &&
                !string.Equals(asset.Sha256, sha, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(finalPath);
                return new DownloadResult { Success = false, Error = "Checksum mismatch." };
            }

            return new DownloadResult { Success = true, InstalledPath = finalPath, Sha256 = sha };
        }
        catch (Exception ex)
        {
            return new DownloadResult { Success = false, Error = ex.Message };
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    private async Task DownloadToFileAsync(
        string url, string destination, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var dest = File.Create(destination);

        var buffer = new byte[BufferSize];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            progress?.Report(new DownloadProgress(received, total));
        }
    }

    private static void ExtractEntry(string zipPath, string entryName, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry(entryName)
            ?? archive.Entries.FirstOrDefault(e =>
                   string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase))
            ?? archive.Entries.FirstOrDefault(e =>
                   e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                   $"'{entryName}' not found in downloaded archive.");

        entry.ExtractToFile(destination, overwrite: true);
    }

    /// <summary>Extract a single nested executable (matched by leaf name) from a .tar.xz and mark it
    /// executable. Uses the system <c>tar</c> (xz support via liblzma) — universally present on Linux,
    /// and the same toolchain the disc-image path already relies on.</summary>
    private static void ExtractTarXzBinary(string tarXzPath, string memberLeafName, string destination)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("tar")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Match the member anywhere in the archive by its leaf name (e.g. ffmpeg-*/bin/ffmpeg) and
        // stream just that one file to stdout, which we write to the destination.
        psi.ArgumentList.Add("-xJf");
        psi.ArgumentList.Add(tarXzPath);
        psi.ArgumentList.Add("--wildcards");
        psi.ArgumentList.Add("--to-stdout");
        psi.ArgumentList.Add("*/" + memberLeafName);

        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Couldn't start 'tar' to extract the download.");
        using (var outFile = File.Create(destination))
            p.StandardOutput.BaseStream.CopyTo(outFile);
        p.WaitForExit();
        if (p.ExitCode != 0 || new FileInfo(destination).Length == 0)
            throw new InvalidOperationException(
                $"'{memberLeafName}' not found in the downloaded archive (tar exit {p.ExitCode}).");

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
