using System;
using System.Runtime.InteropServices;

namespace EmuDOS.Effects.Egl;

/// <summary>
/// A headless OpenGL device: a surfaceless (pbuffer-backed) EGL context plus the handful of GL 3.3
/// entry points the shader path needs, loaded via eglGetProcAddress. Used to run librashader's GL
/// filter chain over a CPU frame off-screen (upload texture → filter → read back). One context per
/// instance, owned by the emulation thread; call <see cref="MakeCurrent"/> before any GL call.
/// Returns null from <see cref="TryCreate"/> if EGL/GL isn't usable, so the caller falls back to the
/// raw frame and the app never depends on a GPU being present.
/// </summary>
public sealed class GlDevice : IDisposable
{
    // ── EGL ──
    private const int EGL_OPENGL_API = 0x30A2;
    private const int EGL_OPENGL_BIT = 0x0008;
    private const int EGL_RENDERABLE_TYPE = 0x3040;
    private const int EGL_SURFACE_TYPE = 0x3033, EGL_PBUFFER_BIT = 0x0001;
    private const int EGL_RED_SIZE = 0x3024, EGL_GREEN_SIZE = 0x3023, EGL_BLUE_SIZE = 0x3022, EGL_ALPHA_SIZE = 0x3021;
    private const int EGL_NONE = 0x3038;
    private const int EGL_WIDTH = 0x3057, EGL_HEIGHT = 0x3056;
    private const int EGL_CONTEXT_MAJOR_VERSION = 0x3098, EGL_CONTEXT_MINOR_VERSION = 0x30FB;
    private const int EGL_CONTEXT_OPENGL_PROFILE_MASK = 0x30FD, EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT = 0x0001;

    // ── GL constants the renderer uses ──
    public const uint GL_TEXTURE_2D = 0x0DE1;
    public const uint GL_RGBA8 = 0x8058, GL_RGBA = 0x1908, GL_BGRA = 0x80E1, GL_UNSIGNED_BYTE = 0x1401;
    public const uint GL_TEXTURE_MIN_FILTER = 0x2801, GL_TEXTURE_MAG_FILTER = 0x2800;
    public const uint GL_LINEAR = 0x2601, GL_NEAREST = 0x2600;
    public const uint GL_TEXTURE_WRAP_S = 0x2802, GL_TEXTURE_WRAP_T = 0x2803, GL_CLAMP_TO_EDGE = 0x812F;
    public const uint GL_FRAMEBUFFER = 0x8D40, GL_COLOR_ATTACHMENT0 = 0x8CE0;
    public const uint GL_PACK_ALIGNMENT = 0x0D05, GL_UNPACK_ALIGNMENT = 0x0CF5;

    [DllImport("libEGL.so.1")] private static extern IntPtr eglGetDisplay(IntPtr displayId);
    [DllImport("libEGL.so.1")] private static extern uint eglInitialize(IntPtr dpy, out int major, out int minor);
    [DllImport("libEGL.so.1")] private static extern uint eglBindAPI(uint api);
    [DllImport("libEGL.so.1")] private static extern uint eglChooseConfig(IntPtr dpy, int[] attribs, IntPtr[] configs, int size, out int num);
    [DllImport("libEGL.so.1")] private static extern IntPtr eglCreateContext(IntPtr dpy, IntPtr config, IntPtr share, int[] attribs);
    [DllImport("libEGL.so.1")] private static extern IntPtr eglCreatePbufferSurface(IntPtr dpy, IntPtr config, int[] attribs);
    [DllImport("libEGL.so.1")] private static extern uint eglMakeCurrent(IntPtr dpy, IntPtr draw, IntPtr read, IntPtr ctx);
    [DllImport("libEGL.so.1")] private static extern IntPtr eglGetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport("libEGL.so.1")] private static extern uint eglDestroyContext(IntPtr dpy, IntPtr ctx);
    [DllImport("libEGL.so.1")] private static extern uint eglDestroySurface(IntPtr dpy, IntPtr surface);
    [DllImport("libEGL.so.1")] private static extern uint eglTerminate(IntPtr dpy);

    private IntPtr _dpy, _ctx, _surface;

    // ── GL function delegates ──
    private delegate void GlGenDel(int n, uint[] ids);
    private delegate void GlBindTextureDel(uint target, uint tex);
    private delegate void GlTexImage2DDel(uint target, int level, int internalFmt, int w, int h, int border, uint format, uint type, IntPtr pixels);
    private delegate void GlTexSubImage2DDel(uint target, int level, int x, int y, int w, int h, uint format, uint type, IntPtr pixels);
    private delegate void GlTexParameteriDel(uint target, uint pname, int param);
    private delegate void GlBindFramebufferDel(uint target, uint fb);
    private delegate void GlFramebufferTexture2DDel(uint target, uint attachment, uint textarget, uint tex, int level);
    private delegate void GlReadPixelsDel(int x, int y, int w, int h, uint format, uint type, IntPtr pixels);
    private delegate void GlPixelStoreiDel(uint pname, int param);
    private delegate void GlViewportDel(int x, int y, int w, int h);
    private delegate void GlFinishDel();
    private delegate uint GlGetErrorDel();

    private GlGenDel _genTextures = null!, _genFramebuffers = null!, _deleteTextures = null!, _deleteFramebuffers = null!;
    private GlBindTextureDel _bindTexture = null!;
    private GlTexImage2DDel _texImage2D = null!;
    private GlTexSubImage2DDel _texSubImage2D = null!;
    private GlTexParameteriDel _texParameteri = null!;
    private GlBindFramebufferDel _bindFramebuffer = null!;
    private GlFramebufferTexture2DDel _framebufferTexture2D = null!;
    private GlReadPixelsDel _readPixels = null!;
    private GlPixelStoreiDel _pixelStorei = null!;
    private GlViewportDel _viewport = null!;
    private GlFinishDel _finish = null!;
    private GlGetErrorDel _getError = null!;

    private GlDevice() { }

    /// <summary>Creates a headless GL device, or null if EGL/GL isn't available.</summary>
    public static GlDevice? TryCreate()
    {
        var d = new GlDevice();
        try
        {
            d._dpy = eglGetDisplay(IntPtr.Zero); // EGL_DEFAULT_DISPLAY
            if (d._dpy == IntPtr.Zero || eglInitialize(d._dpy, out _, out _) == 0) { d.Dispose(); return null; }
            if (eglBindAPI(EGL_OPENGL_API) == 0) { d.Dispose(); return null; }

            int[] cfgAttribs =
            {
                EGL_SURFACE_TYPE, EGL_PBUFFER_BIT,
                EGL_RENDERABLE_TYPE, EGL_OPENGL_BIT,
                EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8,
                EGL_NONE,
            };
            var configs = new IntPtr[1];
            if (eglChooseConfig(d._dpy, cfgAttribs, configs, 1, out int num) == 0 || num < 1) { d.Dispose(); return null; }

            int[] ctxAttribs =
            {
                EGL_CONTEXT_MAJOR_VERSION, 3, EGL_CONTEXT_MINOR_VERSION, 3,
                EGL_CONTEXT_OPENGL_PROFILE_MASK, EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT,
                EGL_NONE,
            };
            d._ctx = eglCreateContext(d._dpy, configs[0], IntPtr.Zero, ctxAttribs);
            if (d._ctx == IntPtr.Zero) { d.Dispose(); return null; }

            int[] pbAttribs = { EGL_WIDTH, 16, EGL_HEIGHT, 16, EGL_NONE };
            d._surface = eglCreatePbufferSurface(d._dpy, configs[0], pbAttribs); // may be NO_SURFACE if surfaceless

            if (eglMakeCurrent(d._dpy, d._surface, d._surface, d._ctx) == 0) { d.Dispose(); return null; }

            d.LoadGl();
            return d;
        }
        catch
        {
            d.Dispose();
            return null;
        }
    }

    /// <summary>The eglGetProcAddress pointer librashader's GL loader needs.</summary>
    public IntPtr GetProcAddress(string name) => eglGetProcAddress(name);

    public bool MakeCurrent() => _dpy != IntPtr.Zero && eglMakeCurrent(_dpy, _surface, _surface, _ctx) != 0;

    private T Resolve<T>(string name) where T : Delegate
    {
        var p = eglGetProcAddress(name);
        if (p == IntPtr.Zero && NativeLibrary.TryLoad("libGL.so.1", out var gl))
            NativeLibrary.TryGetExport(gl, name, out p);
        if (p == IntPtr.Zero)
            throw new InvalidOperationException($"GL function {name} unavailable");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    private void LoadGl()
    {
        _genTextures = Resolve<GlGenDel>("glGenTextures");
        _genFramebuffers = Resolve<GlGenDel>("glGenFramebuffers");
        _deleteTextures = Resolve<GlGenDel>("glDeleteTextures");
        _deleteFramebuffers = Resolve<GlGenDel>("glDeleteFramebuffers");
        _bindTexture = Resolve<GlBindTextureDel>("glBindTexture");
        _texImage2D = Resolve<GlTexImage2DDel>("glTexImage2D");
        _texSubImage2D = Resolve<GlTexSubImage2DDel>("glTexSubImage2D");
        _texParameteri = Resolve<GlTexParameteriDel>("glTexParameteri");
        _bindFramebuffer = Resolve<GlBindFramebufferDel>("glBindFramebuffer");
        _framebufferTexture2D = Resolve<GlFramebufferTexture2DDel>("glFramebufferTexture2D");
        _readPixels = Resolve<GlReadPixelsDel>("glReadPixels");
        _pixelStorei = Resolve<GlPixelStoreiDel>("glPixelStorei");
        _viewport = Resolve<GlViewportDel>("glViewport");
        _finish = Resolve<GlFinishDel>("glFinish");
        _getError = Resolve<GlGetErrorDel>("glGetError");
    }

    // ── thin GL wrappers ──
    public uint GenTexture() { var a = new uint[1]; _genTextures(1, a); return a[0]; }
    public uint GenFramebuffer() { var a = new uint[1]; _genFramebuffers(1, a); return a[0]; }
    public void DeleteTexture(uint id) { if (id != 0) _deleteTextures(1, new[] { id }); }
    public void DeleteFramebuffer(uint id) { if (id != 0) _deleteFramebuffers(1, new[] { id }); }
    public void BindTexture(uint tex) => _bindTexture(GL_TEXTURE_2D, tex);
    public void BindFramebuffer(uint fb) => _bindFramebuffer(GL_FRAMEBUFFER, fb);
    public void TexParameter(uint pname, uint value) => _texParameteri(GL_TEXTURE_2D, pname, (int)value);
    public void PixelStore(uint pname, int value) => _pixelStorei(pname, value);
    public void Viewport(int w, int h) => _viewport(0, 0, w, h);
    public void Finish() => _finish();
    public uint GetError() => _getError();

    public void TexImage(int w, int h, uint format, IntPtr pixels) =>
        _texImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, w, h, 0, format, GL_UNSIGNED_BYTE, pixels);

    public void TexSubImage(int w, int h, uint format, IntPtr pixels) =>
        _texSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, format, GL_UNSIGNED_BYTE, pixels);

    public void AttachColor(uint texture) =>
        _framebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, texture, 0);

    public void ReadPixels(int w, int h, uint format, IntPtr pixels) =>
        _readPixels(0, 0, w, h, format, GL_UNSIGNED_BYTE, pixels);

    public void Dispose()
    {
        try
        {
            if (_dpy != IntPtr.Zero)
            {
                eglMakeCurrent(_dpy, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (_surface != IntPtr.Zero) eglDestroySurface(_dpy, _surface);
                if (_ctx != IntPtr.Zero) eglDestroyContext(_dpy, _ctx);
                eglTerminate(_dpy);
            }
        }
        catch { /* best effort */ }
        _dpy = _ctx = _surface = IntPtr.Zero;
    }
}
