using System;
using System.Runtime.InteropServices;

namespace EmuDOS.Core.Libretro;

/// <summary>
/// A libretro OpenGL hardware-render channel for a single core (dosbox_pure): creates an offscreen
/// WGL context + FBO on the calling thread, satisfies the core's <c>SET_HW_RENDER</c> negotiation,
/// and reads the rendered FBO back to a CPU buffer (GL_BGRA == libretro XRGB8888 byte layout) so the
/// existing software present path can display it. Adapted from Emutastic's GL pipeline, simplified to
/// one core's policy (offscreen window, own FBO, readback).
///
/// THREAD AFFINITY: every method must run on the one thread that owns the GL context (the dosbox
/// thread). Create during load, read during run, Destroy on teardown — all on that thread.
/// </summary>
internal sealed class GlHwRender : IDisposable
{
    private readonly Action<int, string>? _log;

    // ── HW-render callback ABI (x64 explicit layout; 4 bytes pad after context_type) ──────────
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ContextResetT();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong GetCurrentFramebufferT();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetProcAddressT([MarshalAs(UnmanagedType.LPStr)] string sym);

    private ContextResetT? _coreContextReset;       // SET BY CORE — we call it after LoadGame
    private ContextResetT? _coreContextDestroy;     // SET BY CORE — we call it on teardown
    private GetCurrentFramebufferT? _getFramebuffer; // SET BY US — core calls it for the FBO id
    private GetProcAddressT? _getProcAddress;        // SET BY US — core calls it to resolve GL symbols
    private GCHandle _fbHandle, _paHandle;

    private IntPtr _dpy, _ctx, _surface; // EGL display / context / pbuffer surface
    private uint _fboId, _fboTex, _fboDepth;
    private int _fboWidth = 640, _fboHeight = 480;
    private bool _coreProfile;

    private IntPtr _readback;       // persistent BGRA buffer (top-down, alpha forced 0xFF)
    private int _readbackBytes;
    private bool _fbLogged, _frameLogged;
    private readonly System.Diagnostics.Stopwatch _rbSw = new();
    private double _rbSumMs, _rbMaxMs;
    private int _rbFrames;

    public bool Active { get; private set; }
    public IntPtr FramePtr => _readback;
    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }

    public GlHwRender(Action<int, string>? log) => _log = log;
    private void Log(string m) => _log?.Invoke(1, "[gl] " + m);

    /// <summary>Handle <c>RETRO_ENVIRONMENT_SET_HW_RENDER</c>: accept an OpenGL context, build it +
    /// an FBO, store the core's reset/destroy callbacks and write our framebuffer/proc-address
    /// pointers back into the struct. Returns false for non-GL requests (caller then refuses HW).</summary>
    public bool Negotiate(IntPtr data)
    {
        if (data == IntPtr.Zero)
            return false;

        uint contextType = (uint)Marshal.ReadInt32(data, 0);
        if (contextType != LibretroConstants.HwContextOpenGL && contextType != LibretroConstants.HwContextOpenGLCore)
        {
            Log($"refusing context_type={contextType} (not OpenGL)");
            return false;
        }
        _coreProfile = contextType == LibretroConstants.HwContextOpenGLCore;

        if (!CreateContext())
            return false;
        LoadGlFunctions();
        CreateFbo(_fboWidth, _fboHeight);

        IntPtr reset = Marshal.ReadIntPtr(data, 8);
        IntPtr destroy = Marshal.ReadIntPtr(data, 48);
        _coreContextReset = reset != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<ContextResetT>(reset) : null;
        _coreContextDestroy = destroy != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<ContextResetT>(destroy) : null;

        _getFramebuffer = () =>
        {
            if (!_fbLogged) { _fbLogged = true; Log($"core called get_current_framebuffer -> {_fboId}"); }
            return _fboId;
        };
        _getProcAddress = ResolveProc;
        _fbHandle = GCHandle.Alloc(_getFramebuffer);
        _paHandle = GCHandle.Alloc(_getProcAddress);
        Marshal.WriteIntPtr(data, 16, Marshal.GetFunctionPointerForDelegate(_getFramebuffer));
        Marshal.WriteIntPtr(data, 24, Marshal.GetFunctionPointerForDelegate(_getProcAddress));

        Active = true;
        Log($"SET_HW_RENDER accepted (coreProfile={_coreProfile}); context_reset deferred to post-LoadGame");
        return true;
    }

    /// <summary>Resize the FBO to fit the core's max geometry, then call the core's context_reset with
    /// our context current. Per the libretro spec this happens after retro_load_game returns.</summary>
    public void PrepareAndReset(int maxWidth, int maxHeight)
    {
        if (!Active)
            return;
        eglMakeCurrent(_dpy, _surface, _surface, _ctx);
        if (maxWidth > _fboWidth || maxHeight > _fboHeight)
            CreateFbo(Math.Max(maxWidth, _fboWidth), Math.Max(maxHeight, _fboHeight));
        Log($"calling context_reset (fbo {_fboWidth}x{_fboHeight})");
        try { _coreContextReset?.Invoke(); } catch (Exception ex) { Log("context_reset threw: " + ex.Message); }
        Log("context_reset done");
    }

    /// <summary>Read the FBO (after retro_run) into the persistent CPU buffer, vertically flipped with
    /// alpha forced opaque, and report the size. Runs on the GL thread with the context current.</summary>
    public bool ReadFrame(int width, int height)
    {
        if (!Active || width <= 0 || height <= 0)
            return false;
        int bytes = width * height * 4;
        if (_readbackBytes != bytes)
        {
            if (_readback != IntPtr.Zero) Marshal.FreeHGlobal(_readback);
            _readback = Marshal.AllocHGlobal(bytes);
            _readbackBytes = bytes;
        }

        int stride = width * 4;
        _rbSw.Restart(); // time the readback to tell our pipeline cost apart from a core/game stutter
        IntPtr tmp = Marshal.AllocHGlobal(bytes);
        try
        {
            Gl.glBindFramebuffer(GlBindReadTarget, _fboId);
            Gl.glReadPixels(0, 0, width, height, Gl.GL_BGRA, Gl.GL_UNSIGNED_BYTE, tmp);
            Gl.glBindFramebuffer(GlBindReadTarget, 0);
            unsafe
            {
                byte* src = (byte*)tmp;       // bottom-up from GL
                byte* dst = (byte*)_readback; // top-down for WPF
                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(src + y * stride, dst + (height - 1 - y) * stride, stride, stride);
                    byte* row = dst + (height - 1 - y) * stride;
                    for (int x = 3; x < stride; x += 4) row[x] = 0xFF; // force opaque (cores leave A=0)
                }
            }
        }
        finally { Marshal.FreeHGlobal(tmp); }

        double ms = _rbSw.Elapsed.TotalMilliseconds;
        _rbSumMs += ms;
        if (ms > _rbMaxMs) _rbMaxMs = ms;
        if (++_rbFrames >= 300)
        {
            Log($"readback avg {_rbSumMs / _rbFrames:0.00} ms, max {_rbMaxMs:0.00} ms over {_rbFrames} frames ({width}x{height})");
            _rbSumMs = 0; _rbMaxMs = 0; _rbFrames = 0;
        }

        if (!_frameLogged)
        {
            _frameLogged = true;
            // Compare our FBO vs the default framebuffer (FBO 0): tells us whether the core actually
            // rendered into the FBO we handed it, or to the default backbuffer instead.
            Log($"first HW frame {width}x{height}: ourFbo(id={_fboId}) nonBlack={SampleNonBlack(_fboId, width, height)}, " +
                $"defaultFbo(0) nonBlack={SampleNonBlack(0, width, height)}");
        }

        FrameWidth = width;
        FrameHeight = height;
        return true;
    }

    private const uint GlBindReadTarget = 0x8CA8; // GL_READ_FRAMEBUFFER

    // Diagnostic: read a framebuffer and report whether any sampled pixel is non-black.
    private bool SampleNonBlack(uint fbo, int width, int height)
    {
        int bytes = width * height * 4;
        IntPtr buf = Marshal.AllocHGlobal(bytes);
        try
        {
            Gl.glBindFramebuffer(GlBindReadTarget, fbo);
            Gl.glReadPixels(0, 0, width, height, Gl.GL_BGRA, Gl.GL_UNSIGNED_BYTE, buf);
            Gl.glBindFramebuffer(GlBindReadTarget, 0);
            unsafe
            {
                byte* p = (byte*)buf;
                for (int i = 0; i + 2 < bytes; i += 4096)
                    if (p[i] != 0 || p[i + 1] != 0 || p[i + 2] != 0) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ── EGL context (Linux) ─────────────────────────────────────────────────────────────────────
    private const int EGL_OPENGL_API = 0x30A2;
    private const int EGL_OPENGL_BIT = 0x0008;
    private const int EGL_RENDERABLE_TYPE = 0x3040;
    private const int EGL_SURFACE_TYPE = 0x3033, EGL_PBUFFER_BIT = 0x0001;
    private const int EGL_RED_SIZE = 0x3024, EGL_GREEN_SIZE = 0x3023, EGL_BLUE_SIZE = 0x3022, EGL_ALPHA_SIZE = 0x3021;
    private const int EGL_DEPTH_SIZE = 0x3025, EGL_STENCIL_SIZE = 0x3026;
    private const int EGL_NONE = 0x3038;
    private const int EGL_WIDTH = 0x3057, EGL_HEIGHT = 0x3056;
    private const int EGL_CONTEXT_MAJOR_VERSION = 0x3098, EGL_CONTEXT_MINOR_VERSION = 0x30FB;
    private const int EGL_CONTEXT_OPENGL_PROFILE_MASK = 0x30FD;
    private const int EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT = 0x0001, EGL_CONTEXT_OPENGL_COMPATIBILITY_PROFILE_BIT = 0x0002;

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

    // ── Context + FBO ─────────────────────────────────────────────────────────────────────────
    private bool CreateContext()
    {
        try
        {
            _dpy = eglGetDisplay(IntPtr.Zero); // EGL_DEFAULT_DISPLAY
            if (_dpy == IntPtr.Zero || eglInitialize(_dpy, out _, out _) == 0) { Log("eglInitialize failed"); return false; }
            if (eglBindAPI(EGL_OPENGL_API) == 0) { Log("eglBindAPI(GL) failed"); return false; }

            int[] cfgAttribs =
            {
                EGL_SURFACE_TYPE, EGL_PBUFFER_BIT,
                EGL_RENDERABLE_TYPE, EGL_OPENGL_BIT,
                EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8,
                EGL_DEPTH_SIZE, 24, EGL_STENCIL_SIZE, 8,
                EGL_NONE,
            };
            var configs = new IntPtr[1];
            if (eglChooseConfig(_dpy, cfgAttribs, configs, 1, out int num) == 0 || num < 1) { Log("eglChooseConfig failed"); return false; }

            int profileBit = _coreProfile ? EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT : EGL_CONTEXT_OPENGL_COMPATIBILITY_PROFILE_BIT;
            int[] ctxAttribs =
            {
                EGL_CONTEXT_MAJOR_VERSION, 3, EGL_CONTEXT_MINOR_VERSION, 3,
                EGL_CONTEXT_OPENGL_PROFILE_MASK, profileBit,
                EGL_NONE,
            };
            _ctx = eglCreateContext(_dpy, configs[0], IntPtr.Zero, ctxAttribs);
            if (_ctx == IntPtr.Zero) // fall back to the other profile
            {
                ctxAttribs[5] = _coreProfile ? EGL_CONTEXT_OPENGL_COMPATIBILITY_PROFILE_BIT : EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT;
                _ctx = eglCreateContext(_dpy, configs[0], IntPtr.Zero, ctxAttribs);
            }
            if (_ctx == IntPtr.Zero) { Log("eglCreateContext failed"); return false; }

            int[] pbAttribs = { EGL_WIDTH, _fboWidth, EGL_HEIGHT, _fboHeight, EGL_NONE };
            _surface = eglCreatePbufferSurface(_dpy, configs[0], pbAttribs);

            if (eglMakeCurrent(_dpy, _surface, _surface, _ctx) == 0) { Log("eglMakeCurrent failed"); return false; }
            Log($"EGL context ready (coreProfile={_coreProfile})");
            return true;
        }
        catch (Exception ex)
        {
            Log("CreateContext (EGL): " + ex.Message);
            return false;
        }
    }

    private void CreateFbo(int width, int height)
    {
        DestroyFbo();
        _fboWidth = width; _fboHeight = height;
        uint[] ids = new uint[1];

        Gl.glGenTextures(1, ids); _fboTex = ids[0];
        Gl.glBindTexture(Gl.GL_TEXTURE_2D, _fboTex);
        Gl.glTexImage2D(Gl.GL_TEXTURE_2D, 0, Gl.GL_RGBA8, width, height, 0, Gl.GL_RGBA, Gl.GL_UNSIGNED_BYTE, IntPtr.Zero);
        Gl.glTexParameteri(Gl.GL_TEXTURE_2D, Gl.GL_TEXTURE_MIN_FILTER, (int)Gl.GL_LINEAR);
        Gl.glTexParameteri(Gl.GL_TEXTURE_2D, Gl.GL_TEXTURE_MAG_FILTER, (int)Gl.GL_LINEAR);
        Gl.glBindTexture(Gl.GL_TEXTURE_2D, 0);

        Gl.glGenRenderbuffers(1, ids); _fboDepth = ids[0];
        Gl.glBindRenderbuffer(Gl.GL_RENDERBUFFER, _fboDepth);
        Gl.glRenderbufferStorage(Gl.GL_RENDERBUFFER, Gl.GL_DEPTH_COMPONENT24, width, height);
        Gl.glBindRenderbuffer(Gl.GL_RENDERBUFFER, 0);

        Gl.glGenFramebuffers(1, ids); _fboId = ids[0];
        Gl.glBindFramebuffer(Gl.GL_FRAMEBUFFER, _fboId);
        Gl.glFramebufferTexture2D(Gl.GL_FRAMEBUFFER, Gl.GL_COLOR_ATTACHMENT0, Gl.GL_TEXTURE_2D, _fboTex, 0);
        Gl.glFramebufferRenderbuffer(Gl.GL_FRAMEBUFFER, Gl.GL_DEPTH_ATTACHMENT, Gl.GL_RENDERBUFFER, _fboDepth);
        uint status = Gl.glCheckFramebufferStatus(Gl.GL_FRAMEBUFFER);
        Log(status == Gl.GL_FRAMEBUFFER_COMPLETE ? $"FBO ok {width}x{height} id={_fboId}" : $"FBO incomplete 0x{status:X}");
        Gl.glBindFramebuffer(Gl.GL_FRAMEBUFFER, 0);
    }

    private IntPtr ResolveProc(string sym)
    {
        IntPtr p = eglGetProcAddress(sym); // EGL 1.5 returns core + extension GL functions
        if (p == IntPtr.Zero && NativeLibrary.TryLoad("libGL.so.1", out var gl))
            NativeLibrary.TryGetExport(gl, sym, out p);
        return p;
    }

    private void LoadGlFunctions() => Gl.Load(ResolveProc);

    private void DestroyFbo()
    {
        uint[] ids = new uint[1];
        if (_fboId != 0) { ids[0] = _fboId; Gl.glDeleteFramebuffers(1, ids); _fboId = 0; }
        if (_fboTex != 0) { ids[0] = _fboTex; Gl.glDeleteTextures(1, ids); _fboTex = 0; }
        if (_fboDepth != 0) { ids[0] = _fboDepth; Gl.glDeleteRenderbuffers(1, ids); _fboDepth = 0; }
    }

    public void Dispose()
    {
        if (!Active && _ctx == IntPtr.Zero)
            return;
        try
        {
            if (_dpy != IntPtr.Zero) eglMakeCurrent(_dpy, _surface, _surface, _ctx);
            try { _coreContextDestroy?.Invoke(); } catch (Exception ex) { Log("context_destroy threw: " + ex.Message); }
            DestroyFbo();
            if (_dpy != IntPtr.Zero)
            {
                eglMakeCurrent(_dpy, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (_surface != IntPtr.Zero) eglDestroySurface(_dpy, _surface);
                if (_ctx != IntPtr.Zero) eglDestroyContext(_dpy, _ctx);
                // Do NOT eglTerminate: _dpy is the process-wide EGL_DEFAULT_DISPLAY shared by every
                // game session, so terminating it here breaks the NEXT launch's hardware context
                // (the "close and re-open" / eglMakeCurrent-failed flapping). Destroying our own
                // context + surface is the correct per-session teardown; the display stays initialised
                // (eglInitialize is idempotent/refcounted, so the next session reuses it cleanly).
            }
        }
        catch (Exception ex) { Log("teardown: " + ex.Message); }
        if (_readback != IntPtr.Zero) { Marshal.FreeHGlobal(_readback); _readback = IntPtr.Zero; }
        if (_fbHandle.IsAllocated) _fbHandle.Free();
        if (_paHandle.IsAllocated) _paHandle.Free();
        _ctx = _surface = _dpy = IntPtr.Zero;
        Active = false;
    }
}
