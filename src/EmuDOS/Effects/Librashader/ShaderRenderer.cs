using System;
using System.IO;
using System.Runtime.InteropServices;
using EmuDOS.Effects.Egl;

namespace EmuDOS.Effects.Librashader;

/// <summary>
/// Runs a downloaded libretro <c>.slangp</c> multi-pass shader preset over a software-rendered emulator
/// frame, via librashader's OpenGL runtime on a headless <see cref="GlDevice"/>, and reads the result
/// back to a CPU BGRA buffer for the existing WriteableBitmap path. The byte[]-in/byte[]-out interface
/// keeps the recording / screenshot / present paths untouched (Linux counterpart of the Windows D3D11
/// renderer).
///
/// THREADING: owned entirely by the emulation thread (the one that submits video frames). The GL
/// context is single-threaded; only touch this from that thread.
/// </summary>
public sealed class ShaderRenderer : IDisposable
{
    private GlDevice? _gl;
    private IntPtr _chain; // libra_gl_filter_chain_t
    private LibrashaderInterop.GlLoaderDelegate? _loader; // kept alive while the chain lives

    private uint _inTex;
    private int _inW, _inH;

    private uint _outTex, _outFbo;
    private int _outW, _outH;

    private byte[] _outBuffer = Array.Empty<byte>();
    private byte[] _staging = Array.Empty<byte>();
    private ulong _frameCount;

    public bool IsReady { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Loads librashader (if needed), creates a GL device, and builds the filter chain for
    /// <paramref name="presetPath"/>. Returns false on any failure. Call on the emulation thread.</summary>
    public bool Initialize(string librashaderPath, string presetPath)
    {
        try
        {
            if (!LibrashaderInterop.Load(librashaderPath))
            {
                LastError = "librashader not available";
                return false;
            }
            // ABI guard: librashader ABIs are NOT backwards-compatible. Our bindings are ABI 2.
            if ((ulong)LibrashaderInterop.AbiVersion() != 2)
            {
                LastError = "librashader ABI mismatch (need 2)";
                return false;
            }
            if (string.IsNullOrWhiteSpace(presetPath) || !File.Exists(presetPath))
            {
                LastError = "preset not found";
                return false;
            }

            _gl = GlDevice.TryCreate();
            if (_gl is null)
            {
                LastError = "no OpenGL/EGL device";
                return false;
            }

            // Parse preset, then build the chain (the create call consumes/invalidates the preset).
            IntPtr err = LibrashaderInterop.PresetCreate(presetPath, out IntPtr preset);
            int code = LibrashaderInterop.ConsumeError(err);
            if (code != 0 || preset == IntPtr.Zero)
            {
                LastError = $"preset_create failed (errno {code})";
                return false;
            }

            _loader = NamePtr =>
            {
                var name = Marshal.PtrToStringUTF8(NamePtr);
                return name is null ? IntPtr.Zero : _gl!.GetProcAddress(name);
            };
            err = LibrashaderInterop.GlChainCreate(ref preset, _loader, IntPtr.Zero, out _chain);
            code = LibrashaderInterop.ConsumeError(err);
            if (code != 0 || _chain == IntPtr.Zero)
            {
                LastError = $"filter_chain_create failed (errno {code})";
                return false;
            }

            IsReady = true;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Dispose();
            return false;
        }
    }

    /// <summary>Runs the shader chain over one frame and returns a tightly-packed BGRA32 buffer at the
    /// shaded output resolution. Returns null on failure (caller falls back to the raw frame). Emu
    /// thread only.</summary>
    public byte[]? Process(byte[] frame, int width, int height, int srcPitch, bool isBgr32, out int outW, out int outH)
    {
        outW = 0; outH = 0;
        if (!IsReady || _gl is null || width <= 0 || height <= 0)
            return null;

        try
        {
            if (!_gl.MakeCurrent())
                return null;

            EnsureInput(width, height);
            EnsureOutput(width, height);
            UploadFrame(frame, width, height, srcPitch, isBgr32);

            var input = new LibrashaderInterop.LibraImageGl
            { Handle = _inTex, Format = GlDevice.GL_RGBA8, Width = (uint)_inW, Height = (uint)_inH };
            var output = new LibrashaderInterop.LibraImageGl
            { Handle = _outTex, Format = GlDevice.GL_RGBA8, Width = (uint)_outW, Height = (uint)_outH };
            var vp = new LibrashaderInterop.LibraViewport { X = 0, Y = 0, Width = (uint)_outW, Height = (uint)_outH };

            _gl.Viewport(_outW, _outH);
            IntPtr err = LibrashaderInterop.GlChainFrame(
                ref _chain, (UIntPtr)_frameCount, input, output, ref vp, IntPtr.Zero, IntPtr.Zero);
            _frameCount++;
            int code = LibrashaderInterop.ConsumeError(err);
            if (code != 0) { LastError = $"filter_chain_frame errno {code}"; return null; }

            ReadBack();
            outW = _outW; outH = _outH;
            return _outBuffer;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    private void EnsureInput(int w, int h)
    {
        if (_inTex != 0 && _inW == w && _inH == h) return;
        _gl!.DeleteTexture(_inTex);
        _inW = w; _inH = h;
        _inTex = _gl.GenTexture();
        _gl.BindTexture(_inTex);
        _gl.TexParameter(GlDevice.GL_TEXTURE_MIN_FILTER, GlDevice.GL_NEAREST);
        _gl.TexParameter(GlDevice.GL_TEXTURE_MAG_FILTER, GlDevice.GL_NEAREST);
        _gl.TexParameter(GlDevice.GL_TEXTURE_WRAP_S, GlDevice.GL_CLAMP_TO_EDGE);
        _gl.TexParameter(GlDevice.GL_TEXTURE_WRAP_T, GlDevice.GL_CLAMP_TO_EDGE);
        _gl.TexImage(w, h, GlDevice.GL_RGBA, IntPtr.Zero);
    }

    private void EnsureOutput(int inW, int inH)
    {
        // Target ~720 lines so multi-pass CRT/scanline detail resolves, capped to keep readback bounded.
        int scale = Math.Clamp((int)Math.Round(720.0 / inH), 1, 4);
        int w = inW * scale, h = inH * scale;
        if (_outTex != 0 && _outW == w && _outH == h) return;
        _gl!.DeleteFramebuffer(_outFbo);
        _gl.DeleteTexture(_outTex);
        _outW = w; _outH = h;

        _outTex = _gl.GenTexture();
        _gl.BindTexture(_outTex);
        _gl.TexParameter(GlDevice.GL_TEXTURE_MIN_FILTER, GlDevice.GL_LINEAR);
        _gl.TexParameter(GlDevice.GL_TEXTURE_MAG_FILTER, GlDevice.GL_LINEAR);
        _gl.TexParameter(GlDevice.GL_TEXTURE_WRAP_S, GlDevice.GL_CLAMP_TO_EDGE);
        _gl.TexParameter(GlDevice.GL_TEXTURE_WRAP_T, GlDevice.GL_CLAMP_TO_EDGE);
        _gl.TexImage(w, h, GlDevice.GL_RGBA, IntPtr.Zero);

        _outFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(_outFbo);
        _gl.AttachColor(_outTex);
        _gl.BindFramebuffer(0);
    }

    private void UploadFrame(byte[] frame, int w, int h, int srcPitch, bool isBgr32)
    {
        _gl!.BindTexture(_inTex);
        _gl.PixelStore(GlDevice.GL_UNPACK_ALIGNMENT, 1);

        // The core frame is BGRA8888 (EmuDOS always submits isBgr32); GL_BGRA upload avoids a CPU swizzle.
        // Tightly-packed rows are required, so repack if the source pitch exceeds the row width.
        int rowBytes = w * 4;
        byte[] tight;
        if (isBgr32 && srcPitch == rowBytes)
        {
            tight = frame;
        }
        else if (isBgr32)
        {
            if (_staging.Length < rowBytes * h) _staging = new byte[rowBytes * h];
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(frame, y * srcPitch, _staging, y * rowBytes, rowBytes);
            tight = _staging;
        }
        else
        {
            // RGB565 → BGRA8888.
            if (_staging.Length < rowBytes * h) _staging = new byte[rowBytes * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int si = y * srcPitch + x * 2;
                    ushort p = (ushort)(frame[si] | (frame[si + 1] << 8));
                    int di = y * rowBytes + x * 4;
                    _staging[di + 0] = (byte)(((p & 0x1F) * 255 + 15) / 31);        // B
                    _staging[di + 1] = (byte)((((p >> 5) & 0x3F) * 255 + 31) / 63); // G
                    _staging[di + 2] = (byte)((((p >> 11) & 0x1F) * 255 + 15) / 31);// R
                    _staging[di + 3] = 255;                                         // A
                }
            tight = _staging;
        }

        var handle = GCHandle.Alloc(tight, GCHandleType.Pinned);
        try { _gl.TexSubImage(w, h, GlDevice.GL_BGRA, handle.AddrOfPinnedObject()); }
        finally { handle.Free(); }
    }

    private void ReadBack()
    {
        int stride = _outW * 4;
        int needed = stride * _outH;
        if (_outBuffer.Length != needed)
            _outBuffer = new byte[needed];

        _gl!.BindFramebuffer(_outFbo);
        _gl.PixelStore(GlDevice.GL_PACK_ALIGNMENT, 1);
        _gl.Finish();

        // glReadPixels returns rows bottom-up; the WriteableBitmap is top-down, so read into a scratch
        // and flip vertically. Read as BGRA to match the display buffer with no CPU swizzle.
        if (_staging.Length < needed) _staging = new byte[needed];
        var handle = GCHandle.Alloc(_staging, GCHandleType.Pinned);
        try { _gl.ReadPixels(_outW, _outH, GlDevice.GL_BGRA, handle.AddrOfPinnedObject()); }
        finally { handle.Free(); }
        _gl.BindFramebuffer(0);

        for (int y = 0; y < _outH; y++)
            Buffer.BlockCopy(_staging, (_outH - 1 - y) * stride, _outBuffer, y * stride, stride);
    }

    public void Dispose()
    {
        try
        {
            if (_chain != IntPtr.Zero && LibrashaderInterop.Loaded)
            {
                IntPtr c = _chain; _chain = IntPtr.Zero;
                var err = LibrashaderInterop.GlChainFree(ref c);
                LibrashaderInterop.ConsumeError(err);
            }
        }
        catch { /* best effort */ }

        try
        {
            if (_gl is not null)
            {
                _gl.MakeCurrent();
                _gl.DeleteTexture(_inTex);
                _gl.DeleteTexture(_outTex);
                _gl.DeleteFramebuffer(_outFbo);
            }
        }
        catch { /* best effort */ }

        _gl?.Dispose();
        _gl = null;
        _inTex = _outTex = _outFbo = 0;
        _loader = null;
        IsReady = false;
    }
}
