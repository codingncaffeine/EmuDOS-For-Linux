using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
/// In-game hotkeys (screenshots F12, recording F9, save/load state F5/F8, fast-forward/slow-mo F6/F7,
/// rewind F4, pause, FPS toggle F1, mouse-lock middle-click, Ctrl+V paste, the L3/F10 disc-swap menu)
/// are wired here. Still deferred (see notes/PHASE4-BACKLOG.md): CRT shaders (F3) and the cheat engine
/// (F11) show a placeholder hint; 3dfx hardware rendering, SDL3 gamepad input, and the MT-32 LCD window.
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
    private bool _showFps = true; // on by default; the FPS key toggles it

    private Core.Media.RecordingService? _recorder;
    private int _recWidth, _recHeight;
    private int _sampleRate = 48000;

    private readonly object _inputLock = new();
    private readonly HashSet<DosKey> _keysDown = [];
    private readonly ConcurrentQueue<KeyEvent> _keyEvents = new();
    private const double MouseSensitivity = 1.5;
    private double _mouseAccumX, _mouseAccumY;
    private double _sensitivity = MouseSensitivity;
    private bool _mouseLeft, _mouseRight;
    private bool _mouseLocked;
    private Point? _lastPointer;
    private bool _capsLock;
    private volatile bool _menuHeld; // mapped to the gamepad L3 button, which opens dosbox's disc menu
    private bool _isPaused;

    // Hotkeys (from UserSettings, with the Windows defaults). Held hotkeys are tracked so toggles
    // (FPS/shader/pause) fire once per physical press — Avalonia has no KeyEventArgs.IsRepeat.
    private Key _fpsKey, _screenshotKey, _recordKey, _menuKey, _saveStateKey, _loadStateKey;
    private Key _cheatKey, _fastForwardKey, _slowMotionKey, _pauseKey, _rewindKey, _shaderCycleKey;
    private Key? _mouseLockKey;
    private readonly HashSet<Key> _heldHotkeys = [];
    private string _desiredPreset = ""; // per-game CRT preset; the renderer lands with the shader phase

    private DispatcherTimer? _hintTimer;

    private static Key ParseKey(string name, Key fallback) => Enum.TryParse<Key>(name, out var k) ? k : fallback;

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

        var settings = services.Settings;
        _screenshotKey = ParseKey(settings.ScreenshotKey, Key.F12);
        _recordKey = ParseKey(settings.RecordKey, Key.F9);
        _mouseLockKey = Enum.TryParse<Key>(settings.MouseLockKey, out var mk) ? mk : null;
        _menuKey = ParseKey(settings.MenuKey, Key.F10);
        _saveStateKey = ParseKey(settings.SaveStateKey, Key.F5);
        _loadStateKey = ParseKey(settings.LoadStateKey, Key.F8);
        _cheatKey = ParseKey(settings.CheatKey, Key.F11);
        _fastForwardKey = ParseKey(settings.FastForwardKey, Key.F6);
        _slowMotionKey = ParseKey(settings.SlowMotionKey, Key.F7);
        _pauseKey = ParseKey(settings.PauseKey, Key.Pause);
        _rewindKey = ParseKey(settings.RewindKey, Key.F4);
        _shaderCycleKey = ParseKey(settings.ShaderCycleKey, Key.F3);
        _fpsKey = ParseKey(settings.FpsOverlayKey, Key.F1);

        var state = services.Store.ReadState(instance.GameboxPath);
        _desiredPreset = state.Shader ?? "";
        if (state.WindowWidth is int w and > 200 && state.WindowHeight is int h and > 150)
        {
            Width = w;
            Height = h;
        }

        // Holding fast-forward/slow-motion/rewind across a focus change would otherwise stick; reset it.
        Deactivated += (_, _) =>
        {
            _session?.SetSpeed(1.0);
            _session?.SetRewinding(false);
            if (_mouseLocked) ToggleMouseLock();
        };

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
            long frames = now - _lastFramesPresented; // presented frames over the last ~1s = output FPS
            _lastFramesPresented = now;
            if (!_showFps)
                return;
            int lockFps = _instance.Profile.Machine.FpsLock;
            FpsOverlay.Text = lockFps > 0 ? $"FPS {frames} / {lockFps}" : $"FPS {frames}";
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
        _sampleRate = sampleRate;
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

    public void SubmitAudioFrames(ReadOnlySpan<short> interleavedStereo)
    {
        _audio?.Submit(interleavedStereo);
        var recorder = _recorder;
        if (recorder?.IsRecording == true && !interleavedStereo.IsEmpty)
        {
            var bytes = MemoryMarshal.AsBytes(interleavedStereo);
            if (_audioBytes.Length < bytes.Length)
                _audioBytes = new byte[bytes.Length];
            bytes.CopyTo(_audioBytes);
            recorder.WriteAudio(_audioBytes, bytes.Length);
        }
    }

    private byte[] _audioBytes = [];

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

            // Recording: feed the raw native frame (FFmpeg nearest-upscales to the displayed size).
            var recorder = _recorder;
            if (recorder?.IsRecording == true && w == _recWidth && h == _recHeight)
                recorder.WriteVideoFrame(_frameBuffer, w * h * 4);
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

    // L3 (the menu key, held) opens dosbox's disc-swap menu. Other pad buttons are deferred (SDL3
    // gamepad reading is Phase 4 — backlog C; XInput is a no-op on Linux).
    public bool IsButtonDown(int port, PadButton button) => button == PadButton.L3 && _menuHeld;

    // ── Input events ────────────────────────────────────────────────────────────────────────
    // Bound hotkeys are handled here and NOT forwarded to the game; everything else types into DOS.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var effective = e.Key;
        bool firstPress = _heldHotkeys.Add(effective); // false = OS auto-repeat (Avalonia has no IsRepeat)

        if (effective == Key.CapsLock && firstPress)
            _capsLock = !_capsLock;

        // Ctrl+V types clipboard text into DOS (Boxer-style paste); the game never sees the shortcut.
        if (effective == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = PasteFromClipboardAsync();
            e.Handled = true;
            return;
        }
        if (effective == _screenshotKey) { CaptureScreenshot(); e.Handled = true; return; }
        if (effective == _recordKey) { if (firstPress) ToggleRecording(); e.Handled = true; return; }
        if (_mouseLockKey is { } mlk && effective == mlk) { if (firstPress) ToggleMouseLock(); e.Handled = true; return; }
        if (effective == _menuKey)
        {
            // Held = the L3 button (see IsButtonDown), which opens dosbox's menu — where CDs/disks are
            // swapped. Lets you change the inserted disc from inside a booted OS.
            _menuHeld = true;
            e.Handled = true;
            return;
        }
        if (effective == _saveStateKey) { if (firstPress) QuickSaveState(); e.Handled = true; return; }
        if (effective == _loadStateKey) { if (firstPress) QuickLoadState(); e.Handled = true; return; }
        if (effective == _cheatKey) { if (firstPress) OpenCheats(); e.Handled = true; return; }
        if (effective == _fastForwardKey) { _session.SetSpeed(4.0); e.Handled = true; return; } // hold
        if (effective == _slowMotionKey) { _session.SetSpeed(0.5); e.Handled = true; return; }   // hold
        if (effective == _rewindKey) { _session.SetRewinding(true); e.Handled = true; return; }   // hold
        if (effective == _fpsKey)
        {
            if (firstPress)
            {
                _showFps = !_showFps;
                FpsOverlay.IsVisible = _showFps;
                if (_showFps) FpsOverlay.Text = "FPS …";
            }
            e.Handled = true;
            return;
        }
        if (effective == _shaderCycleKey) { if (firstPress) CycleShader(); e.Handled = true; return; }
        if (effective == _pauseKey) { if (firstPress) TogglePause(); e.Handled = true; return; }

        var key = KeyMap.ToDosKey(effective);
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
        var effective = e.Key;
        _heldHotkeys.Remove(effective);

        if (effective == _menuKey) { _menuHeld = false; e.Handled = true; return; }
        if (effective == _fastForwardKey || effective == _slowMotionKey)
        {
            _session.SetSpeed(1.0); // release returns to normal speed
            e.Handled = true;
            return;
        }
        if (effective == _rewindKey) { _session.SetRewinding(false); e.Handled = true; return; }

        var key = KeyMap.ToDosKey(effective);
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
                _mouseAccumX += (p.X - last.X) * _sensitivity;
                _mouseAccumY += (p.Y - last.Y) * _sensitivity;
            }
        }
        _lastPointer = p;
    }

    protected override void OnPointerExited(PointerEventArgs e) => _lastPointer = null;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // Middle button is the host mouse-lock toggle (not forwarded to the game).
        if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            ToggleMouseLock();
            e.Handled = true;
            return;
        }
        UpdateButtons(e);
        Focus();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) => UpdateButtons(e);

    // DOS games don't read the wheel, so it's free for adjusting mouse sensitivity (matches Windows).
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _sensitivity = Math.Clamp(_sensitivity + (e.Delta.Y > 0 ? 0.25 : -0.25), 0.25, 6.0);
        ShowHint($"Mouse sensitivity {_sensitivity:0.00}×");
        e.Handled = true;
    }

    private void UpdateButtons(PointerEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        lock (_inputLock)
        {
            _mouseLeft = props.IsLeftButtonPressed;
            _mouseRight = props.IsRightButtonPressed;
        }
    }

    private void ToggleMouseLock()
    {
        _mouseLocked = !_mouseLocked;
        // Avalonia/X11 has no cursor-warp equivalent of the Windows raw-input path, so locked mode
        // hides the cursor and keeps feeding relative deltas (infinite-turn warp is a known Linux gap,
        // tracked in notes/PHASE4-BACKLOG.md section B).
        Cursor = new Cursor(_mouseLocked ? StandardCursorType.None : StandardCursorType.Arrow);
        ShowHint(_mouseLocked ? "Mouse locked — middle-click to release" : "Mouse unlocked");
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

    // ── Hotkey actions ────────────────────────────────────────────────────────────────────────

    /// <summary>Type clipboard text into DOS as paced keystrokes (Boxer-style paste). Bound to Ctrl+V.</summary>
    private async Task PasteFromClipboardAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        string? text = null;
        if (top?.Clipboard is { } cb && await cb.TryGetDataAsync() is { } data)
            text = await data.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
            return;
        if (text.Length > 4096)
            text = text[..4096]; // commands/serials are short — cap to avoid flooding the BIOS buffer

        var chars = new Queue<char>(text);
        DosKey? holding = null;
        ushort holdMods = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            // Release the previously pressed key one tick after pressing it — a clean, paced tap.
            if (holding is { } h)
            {
                _keyEvents.Enqueue(new KeyEvent(false, (uint)h, 0, holdMods));
                holding = null;
                return;
            }
            if (chars.Count == 0)
            {
                timer.Stop();
                return;
            }
            var mapped = CharToKey(chars.Dequeue());
            if (mapped is null)
                return; // unmappable character — skip it and continue next tick
            var (key, shift) = mapped.Value;
            holdMods = shift ? (ushort)KeyModifier.Shift : (ushort)0;
            _keyEvents.Enqueue(new KeyEvent(true, (uint)key, CharFor(key, shift, false), holdMods));
            holding = key;
        };
        timer.Start();
    }

    // Map a character to the DOS key (and whether Shift is needed) that produces it (US layout).
    private static (DosKey Key, bool Shift)? CharToKey(char c)
    {
        if (c is >= 'a' and <= 'z') return (DosKey.A + (c - 'a'), false);
        if (c is >= 'A' and <= 'Z') return (DosKey.A + (c - 'A'), true);
        if (c is >= '0' and <= '9') return (DosKey.D0 + (c - '0'), false);
        return c switch
        {
            ' ' => (DosKey.Space, false),
            '\n' or '\r' => (DosKey.Enter, false),
            '\t' => (DosKey.Tab, false),
            '-' => (DosKey.Minus, false), '_' => (DosKey.Minus, true),
            '=' => (DosKey.Equals, false), '+' => (DosKey.Equals, true),
            ',' => (DosKey.Comma, false), '<' => (DosKey.Comma, true),
            '.' => (DosKey.Period, false), '>' => (DosKey.Period, true),
            '/' => (DosKey.Slash, false), '?' => (DosKey.Slash, true),
            ';' => (DosKey.Semicolon, false), ':' => (DosKey.Semicolon, true),
            '\'' => (DosKey.Apostrophe, false), '"' => (DosKey.Apostrophe, true),
            '[' => (DosKey.LeftBracket, false), '{' => (DosKey.LeftBracket, true),
            ']' => (DosKey.RightBracket, false), '}' => (DosKey.RightBracket, true),
            '\\' => (DosKey.Backslash, false), '|' => (DosKey.Backslash, true),
            '`' => (DosKey.Backquote, false), '~' => (DosKey.Backquote, true),
            ')' => (DosKey.D0, true), '!' => (DosKey.D1, true), '@' => (DosKey.D2, true),
            '#' => (DosKey.D3, true), '$' => (DosKey.D4, true), '%' => (DosKey.D5, true),
            '^' => (DosKey.D6, true), '&' => (DosKey.D7, true), '*' => (DosKey.D8, true),
            '(' => (DosKey.D9, true),
            _ => null,
        };
    }

    // Capture the current frame to a PNG in the gamebox's media/screenshots folder. The frame buffer
    // already carries any shader, so the displayed bitmap is the final image at its rendered size.
    private void CaptureScreenshot()
    {
        if (_bitmap is null)
            return;
        try
        {
            var dir = new Core.Library.Gamebox(_instance.GameboxPath).ScreenshotsDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{SafeName(_instance.Profile.Title)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
            _bitmap.Save(path);
            ShowHint("Screenshot saved", 1.0);
        }
        catch (Exception ex)
        {
            _log.Info($"Screenshot failed: {ex.Message}");
            ShowHint("Screenshot failed");
        }
    }

    // F9: start/stop video recording. Stop encodes on a background thread so the UI doesn't freeze.
    private void ToggleRecording()
    {
        if (_recorder?.IsRecording == true)
        {
            var recorder = _recorder;
            _recorder = null;
            RecIndicator.IsVisible = false;
            ShowHint("Encoding video…", 2.0);
            Task.Run(() =>
            {
                var path = recorder.Stop();
                Dispatcher.UIThread.Post(() => ShowHint(path is not null ? "Video saved" : "Recording failed"));
            });
            return;
        }

        if (_frameWidth <= 0)
            return;
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null)
        {
            ShowHint("Install FFmpeg (Preferences → Downloads) or your distro's ffmpeg", 2.8);
            return;
        }

        var dir = new Core.Library.Gamebox(_instance.GameboxPath).VideosDir;
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, $"{SafeName(_instance.Profile.Title)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");

        var services = ((App)Application.Current!).Services;
        _recorder = new Core.Media.RecordingService(ffmpeg);
        int dispW = (int)Screen.Bounds.Width & ~1, dispH = (int)Screen.Bounds.Height & ~1; // even dims for the encoder
        _recWidth = _frameWidth;
        _recHeight = _frameHeight;
        var error = _recorder.Start(outPath, _recWidth, _recHeight, _sampleRate, services.Settings.VideoQuality, dispW, dispH);
        if (error is not null)
        {
            _recorder = null;
            ShowHint($"Couldn't record: {error}", 2.5);
            return;
        }
        RecIndicator.IsVisible = true;
        ShowHint("Recording started — F9 to stop", 1.5);
    }

    // The bundled/downloaded ffmpeg if present, else the distro's ffmpeg on PATH (system tool on Linux).
    private string? ResolveFfmpeg()
    {
        var services = ((App)Application.Current!).Services;
        var bundled = services.Downloads.InstalledPath(Core.Downloads.AssetManifest.Ffmpeg);
        if (File.Exists(bundled))
            return bundled;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "ffmpeg");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    // F5 writes a NEW save state each time (never overwrites) into the gamebox's saves; F8 loads newest.
    private void QuickSaveState()
    {
        var data = _session.SaveStateBytes();
        if (data is null)
        {
            ShowHint("Save state failed");
            return;
        }
        try
        {
            Core.Library.SaveStateStore.Write(_instance.SavePath, data, ThumbnailPng());
            ShowHint("Save state written");
        }
        catch (Exception ex)
        {
            _log.Info($"Save state write failed: {ex.Message}");
            ShowHint("Save state failed");
        }
    }

    private void QuickLoadState()
    {
        var newest = Core.Library.SaveStateStore.Newest(_instance.SavePath);
        var data = newest is null ? null : Core.Library.SaveStateStore.ReadState(newest);
        if (data is null)
        {
            ShowHint("No save state to load");
            return;
        }
        ShowHint(_session.LoadStateBytes(data) ? "Save state loaded" : "Load state failed");
    }

    // A small PNG thumbnail of the current frame for a save state (best-effort; null on failure).
    private byte[]? ThumbnailPng()
    {
        try
        {
            if (_bitmap is null)
                return null;
            const int targetW = 320;
            int w = _bitmap.PixelSize.Width, h = _bitmap.PixelSize.Height;
            using var ms = new MemoryStream();
            if (w > targetW)
            {
                int th = Math.Max(1, (int)(h * (targetW / (double)w)));
                using var scaled = _bitmap.CreateScaledBitmap(new PixelSize(targetW, th));
                scaled.Save(ms);
            }
            else
            {
                _bitmap.Save(ms);
            }
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private void TogglePause()
    {
        _isPaused = !_isPaused;
        if (_isPaused)
            _session.Pause();
        else
            _session.Resume();
        ShowHint(_isPaused ? "Paused" : "Resumed");
    }

    // CRT shader cycling — the librashader OpenGL renderer lands with the shader phase (backlog C);
    // the per-game preset is already read/persisted so it lights up once the renderer is wired.
    private void CycleShader() => ShowHint("CRT shaders — coming soon");

    // The cheat engine UI (CheatWindow) lands with the secondary windows (backlog A).
    private void OpenCheats() => ShowHint("Cheats — coming soon");

    private static string SafeName(string title) =>
        string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    private void ShowHint(string text, double seconds = 1.3)
    {
        Hint.Text = text;
        Hint.IsVisible = true;
        _hintTimer ??= new DispatcherTimer();
        _hintTimer.Tick -= OnHintTick;
        _hintTimer.Tick += OnHintTick;
        _hintTimer.Interval = TimeSpan.FromSeconds(seconds);
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void OnHintTick(object? sender, EventArgs e)
    {
        _hintTimer?.Stop();
        Hint.IsVisible = false;
    }

    private void OnClosing(object? sender, EventArgs e)
    {
        _fpsTimer?.Stop();
        _hintTimer?.Stop();
        if (_recorder?.IsRecording == true)
        {
            try { _recorder.Stop(); } catch { } // finalize the recording before tearing the session down
            _recorder = null;
        }
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
