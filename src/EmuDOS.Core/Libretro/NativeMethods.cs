using System.Runtime.InteropServices;

namespace EmuDOS.Core.Libretro;

/// <summary>
/// Cross-platform native module loading for the libretro core. Uses .NET's built-in
/// <see cref="NativeLibrary"/> (dlopen/dlsym on Linux, LoadLibrary/GetProcAddress on Windows), so the
/// same host code loads a <c>.so</c> here and a <c>.dll</c> on Windows. A core's sibling runtime
/// libraries (if any) are resolved by the platform loader from its own directory via RPATH /
/// LD_LIBRARY_PATH; dosbox_pure is self-contained, so no altered search path is needed.
/// </summary>
internal static class NativeMethods
{
    // Kept for source compatibility with the Windows host; ignored on Linux (the dynamic loader uses
    // RPATH / LD_LIBRARY_PATH rather than a per-call search-path flag).
    public const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

    /// <summary>The error string from the most recent failed <see cref="LoadLibraryEx"/> — the Linux
    /// analog of GetLastWin32Error (dlopen's message).</summary>
    public static string? LastError { get; private set; }

    /// <summary>Load a native module, or 0 on failure (with <see cref="LastError"/> set).</summary>
    public static nint LoadLibraryEx(string lpFileName, nint hFile, uint dwFlags)
    {
        try
        {
            LastError = null;
            return NativeLibrary.Load(lpFileName);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return 0;
        }
    }

    /// <summary>Resolve an exported symbol, or 0 if it isn't present.</summary>
    public static nint GetProcAddress(nint hModule, string lpProcName) =>
        NativeLibrary.TryGetExport(hModule, lpProcName, out var addr) ? addr : 0;

    /// <summary>Unload a native module.</summary>
    public static bool FreeLibrary(nint hModule)
    {
        if (hModule != 0)
            NativeLibrary.Free(hModule);
        return true;
    }
}
