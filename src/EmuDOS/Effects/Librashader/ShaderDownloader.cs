using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace EmuDOS.Effects.Librashader;

/// <summary>
/// Downloads the libretro slang shader pack so the CRT shader picker has presets. Unlike Windows
/// (which fetches librashader.dll from the librashader release), on Linux the librashader runtime is
/// a system package (a packaging Depends) or already present at <see cref="AppPaths.LibrashaderDllPath"/>;
/// librashader publishes no Linux release binary to download. So we install the preset pack and verify
/// the runtime is loadable, rather than downloading the engine.
/// </summary>
public static class ShaderDownloader
{
    private const string SlangPackUrl = "https://buildbot.libretro.com/assets/frontend/shaders_slang.zip";

    public static bool IsInstalled(string slangRoot, string runtimePath)
        => File.Exists(Path.Combine(slangRoot, ".installed")) && RuntimeAvailable(runtimePath);

    /// <summary>True if librashader can be loaded — the bundled/downloaded path or the system library.</summary>
    public static bool RuntimeAvailable(string runtimePath)
    {
        if (File.Exists(runtimePath))
            return true;
        foreach (var name in new[] { "librashader.so", "librashader" })
            if (NativeLibrary.TryLoad(name, out var h))
            {
                NativeLibrary.Free(h);
                return true;
            }
        return false;
    }

    /// <summary>Download + install the shader preset pack. Reports coarse progress text; throws on
    /// failure (including when the librashader runtime isn't installed).</summary>
    public static async Task DownloadAsync(string slangRoot, string runtimePath, Action<string>? progress = null)
    {
        if (!RuntimeAvailable(runtimePath))
            throw new InvalidOperationException(
                "The librashader runtime isn't installed. Install the 'librashader' package "
                + "(it's a dependency of the EmuDOS package), then download the shader pack again.");

        Directory.CreateDirectory(Path.GetDirectoryName(slangRoot)!);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.Add("User-Agent", "EmuDOS");

        // Slang preset pack (~2500 .slangp). Extract to temp, then move the real root into place so we
        // don't depend on whether the zip wraps everything in one top-level folder.
        progress?.Invoke("Downloading shader pack…");
        var packZip = Path.Combine(Path.GetTempPath(), "emudos_shaders_slang.zip");
        await DownloadFileAsync(http, SlangPackUrl, packZip);

        progress?.Invoke("Extracting shader pack…");
        var temp = Path.Combine(Path.GetTempPath(), "emudos_slang_" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(packZip, temp);
        var realRoot = temp;
        while (Directory.GetFiles(realRoot).Length == 0 && Directory.GetDirectories(realRoot).Length == 1)
            realRoot = Directory.GetDirectories(realRoot)[0]; // descend single wrapper folders

        if (Directory.Exists(slangRoot))
            Directory.Delete(slangRoot, recursive: true);
        Directory.CreateDirectory(Path.GetDirectoryName(slangRoot)!);
        try { Directory.Move(realRoot, slangRoot); }
        catch { CopyDirectory(realRoot, slangRoot); } // cross-volume fallback
        TryDelete(packZip);
        TryDeleteDir(temp);

        File.WriteAllText(Path.Combine(slangRoot, ".installed"), DateTime.UtcNow.ToString("o"));
        progress?.Invoke("Shaders installed.");
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string dest)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var fs = File.Create(dest);
        await src.CopyToAsync(fs);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), overwrite: true);
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private static void TryDeleteDir(string path) { try { Directory.Delete(path, true); } catch { } }
}
