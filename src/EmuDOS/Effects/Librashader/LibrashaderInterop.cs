using System;
using System.IO;
using System.Runtime.InteropServices;

namespace EmuDOS.Effects.Librashader;

/// <summary>
/// P/Invoke bindings for the librashader C ABI (OpenGL runtime, ABI version 2). librashader is
/// RetroArch's shader runtime as a standalone library; we load it dynamically — the downloaded /
/// system <c>librashader.so</c> isn't resolvable by a static [DllImport] — and bind only the GL +
/// preset + error entry points we use. A returned <c>libra_error_t</c> of <see cref="IntPtr.Zero"/>
/// means success. Signatures verified against include/librashader.h at tag librashader-v0.11.2.
/// </summary>
internal static class LibrashaderInterop
{
    /// <summary>Mirror of <c>libra_viewport_t</c> { float x, y; uint32 width, height; }.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LibraViewport
    {
        public float X;
        public float Y;
        public uint Width;
        public uint Height;
    }

    /// <summary>Mirror of <c>libra_image_gl_t</c> { uint32 handle; uint32 format; uint32 width, height; }
    /// — a GL texture id plus its GL internal format and size. Used for both input and output.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LibraImageGl
    {
        public uint Handle; // GLuint texture
        public uint Format; // GL internal format (e.g. GL_RGBA8 = 0x8058)
        public uint Width;
        public uint Height;
    }

    /// <summary><c>const void *(*libra_gl_loader_t)(const char*)</c> — resolves a GL proc by name.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr GlLoaderDelegate(IntPtr namePtr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr PresetCreateDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr presetOut);

    // 'preset' is consumed (invalidated) by this call — pass by ref; do not free afterwards.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr GlChainCreateDelegate(
        ref IntPtr preset, GlLoaderDelegate loader, IntPtr options, out IntPtr chainOut);

    // 'chain' is a POINTER to the opaque handle — pass by ref. image/out are passed BY VALUE.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr GlChainFrameDelegate(
        ref IntPtr chain, UIntPtr frameCount,
        LibraImageGl image, LibraImageGl output, ref LibraViewport viewport,
        IntPtr mvp, IntPtr options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr GlChainFreeDelegate(ref IntPtr chain);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int ErrorFreeDelegate(ref IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int ErrorErrnoDelegate(IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate UIntPtr AbiVersionDelegate();

    public static PresetCreateDelegate PresetCreate = null!;
    public static GlChainCreateDelegate GlChainCreate = null!;
    public static GlChainFrameDelegate GlChainFrame = null!;
    public static GlChainFreeDelegate GlChainFree = null!;
    public static ErrorFreeDelegate ErrorFree = null!;
    public static ErrorErrnoDelegate ErrorErrno = null!;
    public static AbiVersionDelegate AbiVersion = null!;

    private static IntPtr _handle = IntPtr.Zero;
    private static readonly object _gate = new();

    public static bool Loaded => _handle != IntPtr.Zero;

    /// <summary>Loads librashader from <paramref name="path"/>, then falls back to the system library
    /// (a packaging Depends). Binds the GL + preset + error entry points. Idempotent; false on failure.</summary>
    public static bool Load(string path)
    {
        lock (_gate)
        {
            if (Loaded) return true;
            try
            {
                if (!TryOpen(path, out _handle))
                    return false;

                PresetCreate = Bind<PresetCreateDelegate>("libra_preset_create");
                GlChainCreate = Bind<GlChainCreateDelegate>("libra_gl_filter_chain_create");
                GlChainFrame = Bind<GlChainFrameDelegate>("libra_gl_filter_chain_frame");
                GlChainFree = Bind<GlChainFreeDelegate>("libra_gl_filter_chain_free");
                ErrorFree = Bind<ErrorFreeDelegate>("libra_error_free");
                ErrorErrno = Bind<ErrorErrnoDelegate>("libra_error_errno");
                AbiVersion = Bind<AbiVersionDelegate>("libra_instance_abi_version");
                return true;
            }
            catch
            {
                Unload();
                return false;
            }
        }
    }

    private static bool TryOpen(string preferredPath, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath)
            && NativeLibrary.TryLoad(preferredPath, out handle))
            return true;
        // System fallback (distro package): librashader.so on the loader path.
        foreach (var name in new[] { "librashader.so", "libobs-librashader.so", "librashader" })
            if (NativeLibrary.TryLoad(name, out handle))
                return true;
        handle = IntPtr.Zero;
        return false;
    }

    private static T Bind<T>(string export) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_handle, export));

    public static void Unload()
    {
        lock (_gate)
        {
            if (_handle != IntPtr.Zero)
            {
                try { NativeLibrary.Free(_handle); } catch { /* best effort */ }
                _handle = IntPtr.Zero;
            }
        }
    }

    /// <summary>If <paramref name="error"/> is non-null, reads its errno, frees it, and returns the code
    /// (0 = no error). Always nulls the handle.</summary>
    public static int ConsumeError(IntPtr error)
    {
        if (error == IntPtr.Zero) return 0;
        int code = -1;
        try { code = ErrorErrno(error); } catch { }
        try { ErrorFree(ref error); } catch { }
        return code;
    }
}
