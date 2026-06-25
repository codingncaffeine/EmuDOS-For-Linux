using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using EmuDOS.Core.Engine;
using EmuDOS.Core.Infrastructure;
using EmuDOS.Core.Input;
using EmuDOS.Core.Model;
using EmuDOS.Input;
using EmuDOS.Platform;
using EmuDOS.Services;
using CorePixelFormat = EmuDOS.Core.Engine.PixelFormat;

namespace EmuDOS.Views;

/// <summary>
/// Hosts a running game: it is the engine's <see cref="IEngineHost"/> — turning core video frames into
/// an Avalonia <see cref="WriteableBitmap"/>, playing the core's audio through SDL3, and feeding
/// keyboard/mouse back as input.
/// </summary>
/// <remarks>
/// Phase 3 scope: software DOS games run with picture + audio + keyboard/mouse. The full in-game hotkey
/// set (screenshots, recording, save states, shaders, fast-forward/rewind, mouse-lock, MT-32 LCD, FPS
/// toggle, paste, the L3 disc-swap menu) plus 3dfx hardware rendering and gamepad input are deferred —
/// see notes/PHASE4-BACKLOG.md section B/C. Here every key is forwarded straight to the game.
/// </remarks>
public partial class EmulatorWindow : Window, IEngineHost, IInputSource
{
    private readonly IDosSession _session;
    private readonly GameInstance _instance;
    private readonly AppLog _log;
    private readonly long _gameId;
    private readonly DateTime _sessionStart = DateTime.UtcNow;
    private readonly byte[]? _lut; // brightness/gamma lookup; null = no adjustment (fast path)

    private readonly object _frameLock = new();
    private byte[] _frameBuffer = [];
    private byte[] _nativeBuffer = [];
    private int _frameWidth, _frameHeight;
    private WriteableBitmap? _bitmap;
    private int _renderQueued;

    private SdlAudio? _audio;

    private DispatcherTimer? _fpsTimer;
    private long _lastFramesPresented;

    private readonly object _inputLock = new();
    private readonly HashSet<DosKey> _keysDown = [];
    private readonly ConcurrentQueue<KeyEvent> _keyEvents = new();
    private const double MouseSensitivity = 1.5;
    private double _mouseAccumX, _mouseAccumY;
    private bool _mouseLeft, _mouseRight;
    private Point? _lastPointer;
    private bool _capsLock;

    public EmulatorWindow(IDosEngine engine, GameInstance instance, long gameId = 0, byte[]? initialState = null)
    {
        InitializeComponent();
        _instance = instance;
        _gameId = gameId;
        Title = $"EmuDOS — {instance.Profile.Title}";

        var services = ((App)Application.Current!).Services;
        _log = new AppLog(services.Paths, "emulator.log");
        _log.Info($"Launch '{instance.Profile.Title}' exe={instance.Profile.Launch.Executable ?? "(autoexec)"}");
        _lut = BuildLut(instance.Profile.Display);

        var state = services.Store.ReadState(instance.GameboxPath);
        if (state.WindowWidth is int w and > 200 && state.WindowHeight is int h and > 150)
        {
            Width = w;
            Height = h;
        }

        _session = engine.CreateSession(instance, this);
        if (initialState is not null)
            _session.SetInitialState(initialState);

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        _session.Start();
        Focus();

        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fpsTimer.Tick += (_, _) =>
        {
            long now = _session.FramesPresented;
            FpsOverlay.Text = $"FPS {now - _lastFramesPresented}";
            _lastFramesPresented = now;
        };
        _fpsTimer.Start();
    }

    public IInputSource Input => this;

    // ── IEngineHost ─────────────────────────────────────────────────────────────────────────
    public void SubmitVideoFrame(in VideoFrame frame)
    {
        int w = frame.Width, h = frame.Height;
        if (w <= 0 || h <= 0)
            return;

        int needed = w * h * 4;
        if (_nativeBuffer.Length < needed)
            _nativeBuffer = new byte[needed];
        if (frame.Format == CorePixelFormat.Xrgb8888)
            CopyXrgb8888(frame, w, h);
        else
            CopyRgb565(frame, w, h);
        ApplyLut(w * h);

        lock (_frameLock)
        {
            if (_frameBuffer.Length < needed)
                _frameBuffer = new byte[needed];
            Buffer.BlockCopy(_nativeBuffer, 0, _frameBuffer, 0, needed);
            _frameWidth = w;
            _frameHeight = h;
        }

        if (Interlocked.CompareExchange(ref _renderQueued, 1, 0) == 0)
            Dispatcher.UIThread.Post(RenderFrame, DispatcherPriority.Render);
    }

    public void SetAudioSampleRate(int sampleRate)
    {
        try
        {
            _audio = new SdlAudio(sampleRate);
            _log.Info(_audio.IsOpen ? $"Audio init {sampleRate}Hz (SDL3)" : $"Audio init failed: {_audio.LastError}");
        }
        catch (Exception ex)
        {
            _log.Error($"Audio init failed: {ex.Message}");
        }
    }

    public void SubmitAudioFrames(ReadOnlySpan<short> interleavedStereo) => _audio?.Submit(interleavedStereo);

    public void OnCoreLog(int level, string message)
    {
        var tag = level switch { 0 => "DBG", 1 => "INFO", 2 => "WARN", 3 => "ERR", _ => "LOG" };
        _log.Info($"[core:{tag}] {message}");

        // dosbox reports the running program ("…Program: NAME - …"); remember the one the user launched
        // straight from the shell (what they ran to play) so "ran it once from DOS" sticks on close.
        const string marker = "Program: ";
        int i = message.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0)
            return;
        int start = i + marker.Length;
        int dash = message.IndexOf(" -", start, StringComparison.Ordinal);
        var name = (dash > start ? message[start..dash] : message[start..]).Trim();
        if (name.Length == 0)
            return;
        bool isShell = name.Equals("COMMAND", StringComparison.OrdinalIgnoreCase) || name.Equals("DOSBOX", StringComparison.OrdinalIgnoreCase);
        bool prevWasShell = _prevProgram is null
            || _prevProgram.Equals("COMMAND", StringComparison.OrdinalIgnoreCase)
            || _prevProgram.Equals("DOSBOX", StringComparison.OrdinalIgnoreCase);
        if (prevWasShell && !isShell)
            _lastLaunch = name;
        _prevProgram = name;
    }

    private string? _prevProgram, _lastLaunch;

    // Map the program the user launched to a content executable (DOS-style relative path), skipping
    // setup tools and DOS extenders (those are never what "play the game" means).
    private string? CapturedLaunch()
    {
        if (_lastLaunch is null)
            return null;
        try
        {
            string[] exts = [".exe", ".com", ".bat"];
            var match = Directory.EnumerateFiles(_instance.ContentPath, "*.*", SearchOption.AllDirectories)
                .FirstOrDefault(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant())
                                  && Path.GetFileNameWithoutExtension(f).Equals(_lastLaunch, StringComparison.OrdinalIgnoreCase));
            if (match is null || Core.Import.DosExecutables.IsRuntimeHelper(match))
                return null;
            var n = Path.GetFileNameWithoutExtension(match).ToLowerInvariant();
            if (n.Contains("setup") || n.Contains("install") || n.Contains("config"))
                return null;
            return Path.GetRelativePath(_instance.ContentPath, match).Replace('/', '\\');
        }
        catch { return null; }
    }

    // ── Video conversion (pure; ported verbatim from the Windows host) ────────────────────────
    private void CopyXrgb8888(in VideoFrame frame, int w, int h)
    {
        // XRGB8888 little-endian is byte order B,G,R,X — identical to Bgra8888 with opaque alpha.
        for (int y = 0; y < h; y++)
            Marshal.Copy(frame.Data + (y * frame.Pitch), _nativeBuffer, y * w * 4, w * 4);
    }

    private unsafe void CopyRgb565(in VideoFrame frame, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            var row = (ushort*)(frame.Data + (y * frame.Pitch));
            int dst = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                ushort p = row[x];
                int r = (p >> 11) & 0x1F, g = (p >> 5) & 0x3F, b = p & 0x1F;
                _nativeBuffer[dst++] = (byte)((b << 3) | (b >> 2));
                _nativeBuffer[dst++] = (byte)((g << 2) | (g >> 4));
                _nativeBuffer[dst++] = (byte)((r << 3) | (r >> 2));
                _nativeBuffer[dst++] = 0;
            }
        }
    }

    private static byte[]? BuildLut(DisplaySpec display)
    {
        bool identity = Math.Abs(display.Brightness - 1.0) < 0.001 && Math.Abs(display.Gamma - 1.0) < 0.001;
        if (identity)
            return null;
        double invGamma = 1.0 / Math.Max(0.1, display.Gamma);
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double v = Math.Pow(i / 255.0, invGamma) * display.Brightness;
            lut[i] = (byte)Math.Clamp(v * 255.0, 0.0, 255.0);
        }
        return lut;
    }

    private void ApplyLut(int pixelCount)
    {
        var lut = _lut;
        if (lut is null)
            return;
        var buf = _nativeBuffer;
        int n = pixelCount * 4;
        for (int i = 0; i + 2 < n; i += 4)
        {
            buf[i] = lut[buf[i]];
            buf[i + 1] = lut[buf[i + 1]];
            buf[i + 2] = lut[buf[i + 2]];
        }
    }

    private void RenderFrame()
    {
        Interlocked.Exchange(ref _renderQueued, 0);
        lock (_frameLock)
        {
            int w = _frameWidth, h = _frameHeight;
            if (w <= 0)
                return;

            if (_bitmap is null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
            {
                _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Opaque);
                Screen.Source = _bitmap;
            }

            using var fb = _bitmap.Lock();
            int rowBytes = w * 4;
            if (fb.RowBytes == rowBytes)
            {
                Marshal.Copy(_frameBuffer, 0, fb.Address, rowBytes * h);
            }
            else
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(_frameBuffer, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
            }
        }
        Screen.InvalidateVisual();
    }

    // ── IInputSource ──────────────────────────────────────────────────────────────────────────
    public bool IsKeyDown(DosKey key)
    {
        lock (_inputLock)
            return _keysDown.Contains(key);
    }

    public bool TryDequeueKey(out KeyEvent keyEvent) => _keyEvents.TryDequeue(out keyEvent);

    public MouseDelta PollMouse()
    {
        lock (_inputLock)
        {
            int dx = (int)_mouseAccumX;
            int dy = (int)_mouseAccumY;
            _mouseAccumX -= dx;
            _mouseAccumY -= dy;
            return new MouseDelta(dx, dy, _mouseLeft, _mouseRight, false);
        }
    }

    // Gamepad input is deferred (XInput is a no-op on Linux; SDL3 pad reading is Phase 4 — backlog C).
    public bool IsButtonDown(int port, PadButton button) => false;

    // ── Input events (Phase 3: every key goes to the game; hotkeys are Phase 4 — backlog B) ─────
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.CapsLock)
            _capsLock = !_capsLock;

        var key = KeyMap.ToDosKey(e.Key);
        if (key == DosKey.None)
            return;

        bool isNew;
        lock (_inputLock)
            isNew = _keysDown.Add(key); // false on auto-repeat
        if (isNew)
        {
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            _keyEvents.Enqueue(new KeyEvent(true, (uint)key, CharFor(key, shift, _capsLock), Modifiers(e.KeyModifiers)));
        }
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        var key = KeyMap.ToDosKey(e.Key);
        if (key == DosKey.None)
            return;

        bool wasDown;
        lock (_inputLock)
            wasDown = _keysDown.Remove(key);
        if (wasDown)
            _keyEvents.Enqueue(new KeyEvent(false, (uint)key, 0, Modifiers(e.KeyModifiers)));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_lastPointer is { } last)
        {
            lock (_inputLock)
            {
                _mouseAccumX += (p.X - last.X) * MouseSensitivity;
                _mouseAccumY += (p.Y - last.Y) * MouseSensitivity;
            }
        }
        _lastPointer = p;
    }

    protected override void OnPointerExited(PointerEventArgs e) => _lastPointer = null;

    protected override void OnPointerPressed(PointerPressedEventArgs e) => UpdateButtons(e);

    protected override void OnPointerReleased(PointerReleasedEventArgs e) => UpdateButtons(e);

    private void UpdateButtons(PointerEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        lock (_inputLock)
        {
            _mouseLeft = props.IsLeftButtonPressed;
            _mouseRight = props.IsRightButtonPressed;
        }
    }

    private static ushort Modifiers(KeyModifiers m)
    {
        ushort r = 0;
        if (m.HasFlag(KeyModifiers.Shift)) r |= (ushort)KeyModifier.Shift;
        if (m.HasFlag(KeyModifiers.Control)) r |= (ushort)KeyModifier.Ctrl;
        if (m.HasFlag(KeyModifiers.Alt)) r |= (ushort)KeyModifier.Alt;
        return r;
    }

    /// <summary>The typed character for a key (US layout) — needed for copy-protection / text prompts.</summary>
    private static uint CharFor(DosKey key, bool shift, bool caps)
    {
        if (key is >= DosKey.A and <= DosKey.Z)
        {
            char c = (char)('a' + (key - DosKey.A));
            return shift ^ caps ? char.ToUpperInvariant(c) : c;
        }
        if (key is >= DosKey.Keypad0 and <= DosKey.Keypad9)
            return (uint)('0' + (key - DosKey.Keypad0));
        if (key is >= DosKey.D0 and <= DosKey.D9)
        {
            const string digits = "0123456789";
            const string shifted = ")!@#$%^&*(";
            int i = key - DosKey.D0;
            return shift ? shifted[i] : (uint)digits[i];
        }
        return key switch
        {
            DosKey.Space => ' ',
            DosKey.Enter => '\r',
            DosKey.Tab => '\t',
            DosKey.Backspace => 8,
            DosKey.Minus => shift ? '_' : (uint)'-',
            DosKey.Equals => shift ? '+' : (uint)'=',
            DosKey.Comma => shift ? '<' : (uint)',',
            DosKey.Period => shift ? '>' : (uint)'.',
            DosKey.Slash => shift ? '?' : (uint)'/',
            DosKey.Semicolon => shift ? ':' : (uint)';',
            DosKey.Apostrophe => shift ? '"' : (uint)'\'',
            DosKey.LeftBracket => shift ? '{' : (uint)'[',
            DosKey.RightBracket => shift ? '}' : (uint)']',
            DosKey.Backslash => shift ? '|' : (uint)'\\',
            DosKey.Backquote => shift ? '~' : (uint)'`',
            _ => 0,
        };
    }

    private void OnClosing(object? sender, EventArgs e)
    {
        _fpsTimer?.Stop();
        try { _session.Stop(); } catch { }
        try { _session.Dispose(); } catch { }
        _audio?.Dispose();

        var services = ((App)Application.Current!).Services;
        try
        {
            var st = services.Store.ReadState(_instance.GameboxPath) with
            {
                WindowWidth = (int)Width,
                WindowHeight = (int)Height,
            };
            if (CapturedLaunch() is { } captured)
                st = st with { LastRunProgram = captured };
            services.Store.WriteState(_instance.GameboxPath, st);

            if (_gameId != 0)
                services.Library.AddPlayTime(_gameId, (int)(DateTime.UtcNow - _sessionStart).TotalSeconds);
        }
        catch (Exception ex) { _log.Error($"Close persist failed: {ex.Message}"); }
    }
}
