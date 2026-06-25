using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace EmuDOS.Services;

/// <summary>
/// Plays a looping ScreenScraper video snap (.mp4) into a <see cref="WriteableBitmap"/> via LibVLC's
/// software video callback — no native VideoView, so it renders anywhere in Avalonia (e.g. a hover
/// popup). One instance reuses its buffer/bitmap and MediaPlayer across games (Play swaps the media).
/// Unlike WPF, an Avalonia <see cref="WriteableBitmap"/> doesn't auto-refresh when its pixels change,
/// so each drawn frame raises <see cref="FrameDrawn"/> for the host to invalidate its Image.
/// </summary>
public sealed class SnapPlayer : IDisposable
{
    private const int W = 320, H = 240, Stride = W * 4; // ScreenScraper snaps are 4:3 320x240

    private readonly Dispatcher _ui;
    private readonly IntPtr _buffer;
    private LibVLCSharp.Shared.MediaPlayer? _player;
    private Action? _onFirstFrame;
    private bool _first;
    private volatile bool _disposed;

    public WriteableBitmap Bitmap { get; }

    /// <summary>Raised on the UI thread after each frame is copied into <see cref="Bitmap"/>.</summary>
    public event Action? FrameDrawn;

    public SnapPlayer(Dispatcher ui)
    {
        _ui = ui;
        _buffer = Marshal.AllocHGlobal(Stride * H);
        Bitmap = new WriteableBitmap(new PixelSize(W, H), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Opaque);
    }

    /// <summary>Start (or restart with a new file) the looping snap. <paramref name="onFirstFrame"/>
    /// fires once when the first frame is drawn (use it to reveal the popup).</summary>
    public async void Play(string mp4Path, Action? onFirstFrame)
    {
        var libVLC = await VideoPlaybackService.Instance.GetLibVLCAsync();
        if (libVLC is null || _disposed)
            return;

        await Task.Run(() =>
        {
            try
            {
                _player ??= CreatePlayer(libVLC);
                _onFirstFrame = onFirstFrame;
                _first = true;
                _player.Stop();
                using var media = new Media(libVLC, mp4Path, FromType.FromPath);
                media.AddOption(":input-repeat=65535"); // loop forever
                _player.Play(media);
            }
            catch { /* a hover preview is best-effort */ }
        });
    }

    private LibVLCSharp.Shared.MediaPlayer CreatePlayer(LibVLC libVLC)
    {
        var player = new LibVLCSharp.Shared.MediaPlayer(libVLC);
        player.SetVideoFormat("RV32", W, H, Stride);
        player.SetVideoCallbacks(
            (IntPtr opaque, IntPtr planes) => { Marshal.WriteIntPtr(planes, _buffer); return IntPtr.Zero; },
            null,
            (IntPtr opaque, IntPtr picture) => _ui.Post(() =>
            {
                if (_disposed)
                    return;
                CopyFrame();
                FrameDrawn?.Invoke();
                if (_first)
                {
                    _first = false;
                    _onFirstFrame?.Invoke();
                }
            }));
        return player;
    }

    private unsafe void CopyFrame()
    {
        using var fb = Bitmap.Lock();
        if (fb.RowBytes == Stride)
        {
            Buffer.MemoryCopy((void*)_buffer, (void*)fb.Address, (long)fb.RowBytes * H, (long)Stride * H);
        }
        else
        {
            for (int y = 0; y < H; y++)
                Buffer.MemoryCopy((void*)(_buffer + y * Stride), (void*)(fb.Address + y * fb.RowBytes),
                    fb.RowBytes, Stride);
        }
    }

    public void Stop()
    {
        try { _player?.Stop(); } catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _player?.Stop(); _player?.Dispose(); } catch { }
        _player = null;
        if (_buffer != IntPtr.Zero)
            Marshal.FreeHGlobal(_buffer);
    }
}
