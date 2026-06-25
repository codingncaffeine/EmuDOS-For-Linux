using System;
using System.Runtime.InteropServices;

namespace EmuDOS.Platform;

/// <summary>
/// SDL3-backed audio output — the Linux replacement for the Windows host's NAudio/WASAPI sink.
///
/// The libretro core emits signed-16 stereo PCM at its native rate; SDL3's audio stream resamples to
/// the device internally, so we open at the core's rate and push samples. The dosbox_pure session
/// paces by wall clock, so we don't need dynamic-rate control here — just bound the queued latency by
/// discarding a batch when the backlog grows past a cap (the same intent as NAudio's
/// DiscardOnBufferOverflow), so audio can't drift seconds behind the picture.
/// </summary>
public sealed class SdlAudio : IDisposable
{
    private const uint SDL_INIT_AUDIO = 0x00000010;
    private const uint SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK = 0xFFFFFFFF;
    private const int SDL_AUDIO_S16LE = 0x8010; // signed 16-bit little-endian

    [StructLayout(LayoutKind.Sequential)]
    private struct SDL_AudioSpec
    {
        public int format;
        public int channels;
        public int freq;
    }

    [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_InitSubSystem(uint flags);
    [DllImport("SDL3")] private static extern void SDL_QuitSubSystem(uint flags);
    [DllImport("SDL3")] private static extern IntPtr SDL_OpenAudioDeviceStream(uint devid, in SDL_AudioSpec spec, IntPtr cb, IntPtr userdata);
    [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_ResumeAudioStreamDevice(IntPtr stream);
    [DllImport("SDL3")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_PutAudioStreamData(IntPtr stream, IntPtr buf, int len);
    [DllImport("SDL3")] private static extern int SDL_GetAudioStreamQueued(IntPtr stream);
    [DllImport("SDL3")] private static extern void SDL_DestroyAudioStream(IntPtr stream);
    [DllImport("SDL3")] private static extern IntPtr SDL_GetError();

    private IntPtr _stream;
    private readonly int _sampleRate;
    private readonly int _maxQueuedBytes; // ~400ms cushion cap (S16 stereo = 4 bytes/frame)

    public SdlAudio(int sampleRate)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;
        _maxQueuedBytes = _sampleRate * 4 * 400 / 1000;
        SDL_InitSubSystem(SDL_INIT_AUDIO);

        var spec = new SDL_AudioSpec { format = SDL_AUDIO_S16LE, channels = 2, freq = _sampleRate };
        _stream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, in spec, IntPtr.Zero, IntPtr.Zero);
        if (_stream == IntPtr.Zero)
            return;
        SDL_ResumeAudioStreamDevice(_stream); // streams from SDL_OpenAudioDeviceStream start paused
    }

    public bool IsOpen => _stream != IntPtr.Zero;

    public string? LastError => Marshal.PtrToStringUTF8(SDL_GetError());

    /// <summary>Queue a batch of interleaved S16 stereo samples, dropping it if the device backlog is
    /// already past the latency cap (overflow discard) so audio stays close to the picture.</summary>
    public unsafe void Submit(ReadOnlySpan<short> interleavedStereo)
    {
        if (_stream == IntPtr.Zero || interleavedStereo.IsEmpty)
            return;
        if (SDL_GetAudioStreamQueued(_stream) > _maxQueuedBytes)
            return; // overflow — let the device catch up rather than drift behind
        fixed (short* p = interleavedStereo)
            SDL_PutAudioStreamData(_stream, (IntPtr)p, interleavedStereo.Length * 2);
    }

    public void Dispose()
    {
        if (_stream != IntPtr.Zero)
        {
            SDL_DestroyAudioStream(_stream);
            _stream = IntPtr.Zero;
        }
        SDL_QuitSubSystem(SDL_INIT_AUDIO);
    }
}
