namespace EmuDOS.Core.Downloads;

/// <summary>
/// The catalogue of downloadable third-party assets. dosbox_pure is fetched from the
/// libretro nightly buildbot (as RetroArch/Emutastic do) rather than bundled, to avoid
/// shipping a GPL-licensed binary in our distribution.
/// </summary>
public static class AssetManifest
{
    public const string DosBoxPureFileName = "dosbox_pure_libretro.so";
    public const string CatalogFileName = "catalog.db";

    private const string ReleaseBase = "https://github.com/codingncaffeine/EmuDOS-For-Linux/releases/latest/download";

    /// <summary>The dosbox_pure libretro core (Linux x86-64) — same buildbot lineup as the Windows
    /// app, fetched as a .so instead of a .dll.</summary>
    public static DownloadAsset DosBoxPure { get; } = new()
    {
        Id = "dosbox_pure",
        DisplayName = "DOSBox Pure core",
        Description = "The DOS emulator that runs your games. Required.",
        Url = "https://buildbot.libretro.com/nightly/linux/x86_64/latest/dosbox_pure_libretro.so.zip",
        Kind = DownloadKind.ZippedCore,
        FileName = DosBoxPureFileName,
        Category = AssetCategory.Core,
    };

    /// <summary>The curated config catalog (recognizes games and applies good settings on import).</summary>
    public static DownloadAsset Catalog { get; } = new()
    {
        Id = "catalog",
        DisplayName = "Game catalog",
        Description = "Recognizes imported games and applies curated settings. Recommended.",
        Url = $"{ReleaseBase}/{CatalogFileName}",
        Kind = DownloadKind.File,
        FileName = CatalogFileName,
        Category = AssetCategory.Catalog,
    };

    // Note: the MT-32 synth shim (emudos_mt32.dll) ships WITH the app — it's our own small
    // LGPL-based DLL, so unlike the GPL core there's no reason to download it. The only
    // user-supplied MT-32 piece is the Roland ROMs (copyrighted; detected, never distributed).

    // FFmpeg (video recording) downloads from the Downloads tab exactly like the Windows app — no
    // system package, no terminal. BtbN ships a fully static Linux build as a .tar.xz; we pull the
    // single `ffmpeg` binary out of it (TarXzBinary) and mark it executable. The record path still
    // falls back to a distro ffmpeg on PATH if one happens to be installed (see EmulatorWindow).
    public const string FfmpegFileName = "ffmpeg";

    /// <summary>FFmpeg (GPL) for video recording — downloaded, not bundled, like the core. BtbN's
    /// linux64 GPL build is a static .tar.xz carrying ffmpeg under bin/; TarXzBinary extracts it.</summary>
    public static DownloadAsset Ffmpeg { get; } = new()
    {
        Id = "ffmpeg",
        DisplayName = "FFmpeg (video recording)",
        Description = "Enables recording gameplay video. Optional — only needed for the record feature.",
        Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz",
        Kind = DownloadKind.TarXzBinary,
        FileName = FfmpegFileName,
        Category = AssetCategory.Native,
    };

    /// <summary>All assets the Downloads tab can offer. SDL3 is bundled (gamepads); FFmpeg is optional.</summary>
    public static IReadOnlyList<DownloadAsset> All { get; } = [DosBoxPure, Catalog, Ffmpeg];
}
