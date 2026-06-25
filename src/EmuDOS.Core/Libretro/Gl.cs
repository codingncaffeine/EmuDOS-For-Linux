using System;
using System.Runtime.InteropServices;

namespace EmuDOS.Core.Libretro;

/// <summary>Native WGL/OpenGL/Win32 entry points used by <see cref="GlHwRender"/>. GL 1.1 functions
/// are direct opengl32 exports; FBO/renderbuffer functions are extensions resolved at runtime via
/// <see cref="Load"/>. Single GL context per process (one core), so the extension pointers are static.</summary>
internal static class Gl
{
    // ── WGL / DC ──────────────────────────────────────────────────────────────────────────────
    [DllImport("opengl32.dll")] public static extern IntPtr wglGetProcAddress(string name);
    [DllImport("opengl32.dll")] public static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport("opengl32.dll")] public static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
    [DllImport("opengl32.dll")] public static extern bool wglDeleteContext(IntPtr hglrc);

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] public static extern bool SetPixelFormat(IntPtr hdc, int fmt, ref PIXELFORMATDESCRIPTOR pfd);

    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll")] public static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    public delegate IntPtr WglCreateContextAttribsArb(IntPtr hdc, IntPtr share, int[] attribs);

    // ── GL 1.1 core + FBO/renderbuffer extensions — ALL resolved via Load(). The Windows build could
    //    DllImport the 1.1 core functions from opengl32.dll directly, but on Linux glReadPixels et al.
    //    live in libGL and are resolved through eglGetProcAddress (EGL 1.5 returns core functions), so
    //    everything goes through the loader for portability. ──────────────────────────────────────
    private delegate void GenDel(int n, uint[] ids);
    private delegate void BindDel(uint target, uint id);
    private delegate void FbTex2DDel(uint target, uint attachment, uint textarget, uint texture, int level);
    private delegate void RbStorageDel(uint target, uint internalFormat, int w, int h);
    private delegate void FbRbDel(uint target, uint attachment, uint rbTarget, uint rb);
    private delegate uint CheckDel(uint target);
    private delegate void ReadPixelsDel(int x, int y, int w, int h, uint format, uint type, IntPtr pixels);
    private delegate void TexImage2DDel(uint target, int level, int internalFormat, int w, int h, int border, uint format, uint type, IntPtr data);
    private delegate void TexParameteriDel(uint target, uint pname, int param);

    private static GenDel? _genFbo, _genRb, _delFbo, _delRb, _genTex, _delTex;
    private static BindDel? _bindFbo, _bindRb;
    private static FbTex2DDel? _fbTex2D;
    private static RbStorageDel? _rbStorage;
    private static FbRbDel? _fbRb;
    private static CheckDel? _checkFbo;
    private static ReadPixelsDel? _readPixels;
    private static BindDel? _bindTex;
    private static TexImage2DDel? _texImage2D;
    private static TexParameteriDel? _texParameteri;

    public static void glReadPixels(int x, int y, int w, int h, uint format, uint type, IntPtr pixels) => _readPixels?.Invoke(x, y, w, h, format, type, pixels);
    public static void glGenTextures(int n, uint[] ids) => _genTex?.Invoke(n, ids);
    public static void glBindTexture(uint target, uint texture) => _bindTex?.Invoke(target, texture);
    public static void glDeleteTextures(int n, uint[] ids) => _delTex?.Invoke(n, ids);
    public static void glTexImage2D(uint target, int level, int internalFormat, int w, int h, int border, uint format, uint type, IntPtr data) => _texImage2D?.Invoke(target, level, internalFormat, w, h, border, format, type, data);
    public static void glTexParameteri(uint target, uint pname, int param) => _texParameteri?.Invoke(target, pname, param);

    public static void Load(Func<string, IntPtr> resolve)
    {
        T? R<T>(string name) where T : class
        {
            IntPtr p = resolve(name);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
        }
        _readPixels = R<ReadPixelsDel>("glReadPixels");
        _genTex = R<GenDel>("glGenTextures");
        _bindTex = R<BindDel>("glBindTexture");
        _delTex = R<GenDel>("glDeleteTextures");
        _texImage2D = R<TexImage2DDel>("glTexImage2D");
        _texParameteri = R<TexParameteriDel>("glTexParameteri");
        _genFbo = R<GenDel>("glGenFramebuffers");
        _bindFbo = R<BindDel>("glBindFramebuffer");
        _fbTex2D = R<FbTex2DDel>("glFramebufferTexture2D");
        _genRb = R<GenDel>("glGenRenderbuffers");
        _bindRb = R<BindDel>("glBindRenderbuffer");
        _rbStorage = R<RbStorageDel>("glRenderbufferStorage");
        _fbRb = R<FbRbDel>("glFramebufferRenderbuffer");
        _checkFbo = R<CheckDel>("glCheckFramebufferStatus");
        _delFbo = R<GenDel>("glDeleteFramebuffers");
        _delRb = R<GenDel>("glDeleteRenderbuffers");
    }

    public static void glGenFramebuffers(int n, uint[] ids) => _genFbo?.Invoke(n, ids);
    public static void glBindFramebuffer(uint target, uint id) => _bindFbo?.Invoke(target, id);
    public static void glFramebufferTexture2D(uint t, uint a, uint tt, uint tex, int lvl) => _fbTex2D?.Invoke(t, a, tt, tex, lvl);
    public static void glGenRenderbuffers(int n, uint[] ids) => _genRb?.Invoke(n, ids);
    public static void glBindRenderbuffer(uint target, uint id) => _bindRb?.Invoke(target, id);
    public static void glRenderbufferStorage(uint t, uint fmt, int w, int h) => _rbStorage?.Invoke(t, fmt, w, h);
    public static void glFramebufferRenderbuffer(uint t, uint a, uint rt, uint rb) => _fbRb?.Invoke(t, a, rt, rb);
    public static uint glCheckFramebufferStatus(uint target) => _checkFbo?.Invoke(target) ?? 0;
    public static void glDeleteFramebuffers(int n, uint[] ids) => _delFbo?.Invoke(n, ids);
    public static void glDeleteRenderbuffers(int n, uint[] ids) => _delRb?.Invoke(n, ids);

    // ── Constants ────────────────────────────────────────────────────────────────────────────────
    public const uint GL_FRAMEBUFFER = 0x8D40;
    public const uint GL_RGBA = 0x1908;
    public const int GL_RGBA8 = 0x8058; // used as glTexImage2D internalFormat (int)
    public const uint GL_UNSIGNED_BYTE = 0x1401;
    public const uint GL_BGRA = 0x80E1;
    public const uint GL_TEXTURE_2D = 0x0DE1;
    public const uint GL_TEXTURE_MIN_FILTER = 0x2801;
    public const uint GL_TEXTURE_MAG_FILTER = 0x2800;
    public const uint GL_LINEAR = 0x2601;
    public const uint GL_COLOR_ATTACHMENT0 = 0x8CE0;
    public const uint GL_DEPTH_ATTACHMENT = 0x8D00;
    public const uint GL_RENDERBUFFER = 0x8D41;
    public const uint GL_DEPTH_COMPONENT24 = 0x81A5;
    public const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;

    public const uint PFD_DRAW_TO_WINDOW = 0x00000004;
    public const uint PFD_SUPPORT_OPENGL = 0x00000020;
    public const byte PFD_TYPE_RGBA = 0;

    public const int WGL_CONTEXT_MAJOR_VERSION_ARB = 0x2091;
    public const int WGL_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    public const int WGL_CONTEXT_PROFILE_MASK_ARB = 0x9126;
    public const int WGL_CONTEXT_CORE_PROFILE_BIT_ARB = 0x00000001;
    public const int WGL_CONTEXT_COMPATIBILITY_PROFILE_BIT_ARB = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion;
        public uint dwFlags;
        public byte iPixelType, cColorBits, cRedBits, cRedShift;
        public byte cGreenBits, cGreenShift, cBlueBits, cBlueShift;
        public byte cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits;
        public byte cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
        public byte cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }
}
