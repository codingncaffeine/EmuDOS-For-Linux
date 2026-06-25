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

    // On Linux, FFmpeg and SDL3 are system packages (the .deb/AUR list them as Depends), exactly like
    // libvlc — so they're NOT downloaded the way the Windows app fetches win64 binaries. The Ffmpeg
    // asset is kept only so InstalledPath() resolves; recording falls back to the distro's ffmpeg on
    // PATH when no bundled copy exists (see EmulatorWindow.ResolveFfmpeg). Neither is in All, so the
    // Downloads tab doesn't offer them.
    public const string FfmpegFileName = "ffmpeg";

    /// <summary>FFmpeg (GPL) for video recording — a system package on Linux (the record path resolves
    /// it from PATH). Kept as an asset only for InstalledPath() compatibility; not offered as a download.</summary>
    public static DownloadAsset Ffmpeg { get; } = new()
    {
        Id = "ffmpeg",
        DisplayName = "FFmpeg (video recording)",
        Description = "Enables recording gameplay video. Provided by your distro's ffmpeg package.",
        Url = string.Empty,
        Kind = DownloadKind.File,
        FileName = FfmpegFileName,
        Category = AssetCategory.Native,
    };

    /// <summary>All assets the Downloads tab can offer. FFmpeg and SDL3 are system packages on Linux.</summary>
    public static IReadOnlyList<DownloadAsset> All { get; } = [DosBoxPure, Catalog];
}
