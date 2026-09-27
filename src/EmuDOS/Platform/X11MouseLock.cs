using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using EmuDOS.Core.Input;

namespace EmuDOS.Platform;

/// <summary>
/// True in-game mouse lock on X11 and Xwayland — the recipe SDL2 and GLFW use for "relative mouse mode":
/// <list type="bullet">
/// <item>the game is fed XInput2 <b>raw motion</b> (unaccelerated device counts), which keeps flowing no
/// matter where the cursor is — it can never clip at a window or screen edge;</item>
/// <item>the pointer is <b>grabbed and confined</b> to the game window (<c>XGrabPointer</c> with
/// <c>confine_to</c>), so it cannot wander off onto the desktop and clicks always land in the game;</item>
/// <item>the cursor is <b>hidden with XFixes</b>, which the server sees as a NULL cursor.</item>
/// </list>
/// Under Xwayland (GNOME, KDE, wlroots) "hidden cursor + confined pointer" is exactly the pattern Xwayland
/// turns into a compositor-side pointer lock (<c>zwp_locked_pointer_v1</c>), so it behaves the same as on a
/// native Xorg session. Warping the cursor is deliberately NOT relied on: Xwayland only emulates
/// <c>XWarpPointer</c> when the cursor is a NULL cursor, and a transparent pixmap cursor (what a toolkit's
/// "no cursor" is) does not qualify — which is why the old warp-to-centre lock silently did nothing there.
/// <para>
/// One dedicated thread owns one private X connection; the UI thread only posts Lock/Unlock. Raw deltas and
/// the grabbed button events are delivered through callbacks invoked <b>on that input thread</b>, so nothing
/// here ever runs on the UI thread. Every X call references the window only while it is alive: the owning
/// window disposes this object from its Closing handler, which runs before the X window is destroyed.
/// </para>
/// </summary>
internal sealed unsafe class X11MouseLock : IDisposable
{
    /// <summary>What a Lock/Unlock request came to (reported on the input thread).</summary>
    public enum LockState
    {
        /// <summary>Grab + raw motion engaged; the game owns the mouse.</summary>
        Engaged,
        /// <summary>The lock ended (Unlock, or shutdown).</summary>
        Released,
        /// <summary>The grab could not be taken (no X11, XI too old, or another client holds the pointer).</summary>
        Refused,
    }

    // ── Callbacks (all invoked on the input thread) ──────────────────────────────────────────────
    private readonly Action<double, double> _onRawDelta;   // unaccelerated device counts since the last event
    private readonly Action<int, bool> _onButton;          // X button number (1 left, 2 middle, 3 right, 4/5 wheel), pressed
    private readonly Action<LockState> _onStateChanged;
    private readonly Action<string> _log;

    private readonly ConcurrentQueue<(Command Kind, IntPtr Window)> _commands = new();
    private readonly Thread _thread;
    private readonly int _wakeRead = -1, _wakeWrite = -1; // pipe: the UI thread pokes the input thread's poll()
    private volatile bool _active;

    private enum Command { Lock, Unlock, Quit }

    /// <summary>True while the grab + raw-motion lock is engaged (set by the input thread).</summary>
    public bool IsActive => _active;

    public X11MouseLock(Action<double, double> onRawDelta, Action<int, bool> onButton, Action<LockState> onStateChanged, Action<string> log)
    {
        _onRawDelta = onRawDelta;
        _onButton = onButton;
        _onStateChanged = onStateChanged;
        _log = log;

        int* fds = stackalloc int[2];
        if (pipe(fds) == 0)
        {
            _wakeRead = fds[0];
            _wakeWrite = fds[1];
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "X11MouseLock" };
        _thread.Start();
    }

    /// <summary>
    /// Engage the lock on <paramref name="window"/> (an X window id). Reported back via onStateChanged.
    /// Call it only while the pointer is in the window: a compositor honours a pointer lock solely for
    /// the surface the pointer is in, and under Xwayland an early request (before the compositor has
    /// moved its pointer focus to a freshly mapped window) grabs on the X side only, leaving the real
    /// pointer free to wander off — so the owner waits for the toolkit's enter/move event before asking.
    /// </summary>
    public void Lock(IntPtr window) => Post(Command.Lock, window);

    /// <summary>Release the lock: ungrab, show the cursor, park the pointer at the window centre.</summary>
    public void Unlock() => Post(Command.Unlock, IntPtr.Zero);

    public void Dispose()
    {
        Post(Command.Quit, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2)); // the thread ungrabs + shows the cursor before the X window goes away
        if (_wakeRead >= 0) close(_wakeRead);
        if (_wakeWrite >= 0) close(_wakeWrite);
    }

    private void Post(Command kind, IntPtr window)
    {
        _commands.Enqueue((kind, window));
        if (_wakeWrite >= 0)
        {
            byte b = 1;
            write(_wakeWrite, &b, 1);
        }
    }

    // ── Input thread ─────────────────────────────────────────────────────────────────────────────
    private IntPtr _display, _root, _lockedWindow;
    private int _xiOpcode;

    private void Run()
    {
        if (!Open())
        {
            // No usable X11: drain commands forever, refusing every lock so the window falls back to
            // bounded toolkit deltas.
            while (true)
            {
                WaitForWake(-1);
                while (_commands.TryDequeue(out var c))
                {
                    if (c.Kind == Command.Quit) return;
                    if (c.Kind == Command.Lock) _onStateChanged(LockState.Refused);
                }
            }
        }

        int fd = XConnectionNumber(_display);
        byte* ev = stackalloc byte[XEventSize];
        try
        {
            while (true)
            {
                PollFds(fd, 50);
                DrainWake();

                while (XPending(_display) > 0)
                {
                    XNextEvent(_display, ev);
                    HandleEvent(ev);
                }

                while (_commands.TryDequeue(out var c))
                {
                    switch (c.Kind)
                    {
                        case Command.Lock: DoLock(c.Window); break;
                        case Command.Unlock: DoUnlock(); break;
                        case Command.Quit:
                            DoUnlock();
                            return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log($"mouse lock thread ended: {ex.Message}");
            _active = false;
            _onStateChanged(LockState.Released);
        }
        finally
        {
            try { XCloseDisplay(_display); } catch { }
            _display = IntPtr.Zero;
        }
    }

    private bool Open()
    {
        try
        {
            XInitThreads(); // a no-op once Avalonia has done it; harmless to repeat
            _display = XOpenDisplay(null);
            if (_display == IntPtr.Zero)
            {
                _log("mouse lock: no X11 display — using bounded pointer deltas");
                return false;
            }
            _root = XDefaultRootWindow(_display);

            if (XQueryExtension(_display, "XInputExtension", out _xiOpcode, out _, out _) == 0)
            {
                _log("mouse lock: XInputExtension missing — using bounded pointer deltas");
                return false;
            }
            int major = 2, minor = 2;
            if (XIQueryVersion(_display, ref major, ref minor) != 0 || major < 2 || (major == 2 && minor < 1))
            {
                // XI 2.1 is what makes raw events keep arriving while the pointer is grabbed.
                _log($"mouse lock: XInput {major}.{minor} is too old (need 2.1) — using bounded pointer deltas");
                return false;
            }
            if (XFixesQueryExtension(_display, out _, out _) == 0)
            {
                _log("mouse lock: XFIXES missing — using bounded pointer deltas");
                return false;
            }
            int fixMajor = 0, fixMinor = 0;
            XFixesQueryVersion(_display, ref fixMajor, ref fixMinor);
            if (fixMajor < 4)
            {
                _log($"mouse lock: XFIXES {fixMajor}.{fixMinor} cannot hide the cursor (need 4) — using bounded pointer deltas");
                return false;
            }

            // Raw motion from every master pointer, selected on the root window (raw events are
            // root-only by design). Always on; deltas are only forwarded while the lock is engaged.
            byte* mask = stackalloc byte[4];
            mask[XI_RawMotion >> 3] |= (byte)(1 << (XI_RawMotion & 7));
            var em = new XIEventMask { DeviceId = XIAllMasterDevices, MaskLen = 4, Mask = mask };
            XISelectEvents(_display, _root, &em, 1);
            XFlush(_display);
            _log($"mouse lock: X11 raw input ready (XInput {major}.{minor}, XFIXES {fixMajor}.{fixMinor})");
            return true;
        }
        catch (Exception ex)
        {
            _log($"mouse lock: X11 unavailable ({ex.GetType().Name}: {ex.Message}) — using bounded pointer deltas");
            return false;
        }
    }

    private void HandleEvent(byte* ev)
    {
        int type = *(int*)ev;
        if (type == GenericEvent)
        {
            // XGenericEventCookie overlays the XEvent: extension @32, evtype @36, data @48.
            if (*(int*)(ev + 32) != _xiOpcode || *(int*)(ev + 36) != XI_RawMotion)
                return;
            if (XGetEventData(_display, ev) == 0)
                return;
            try
            {
                if (!_active)
                    return;
                byte* raw = *(byte**)(ev + 48); // XIRawEvent*
                int flags = *(int*)(raw + 60);
                if ((flags & XIPointerEmulated) != 0)
                    return; // synthesized from a touch/tablet event — not a mouse
                int maskLen = *(int*)(raw + 64);
                byte* maskPtr = *(byte**)(raw + 72);
                double* rawValues = *(double**)(raw + 88);
                if (maskLen <= 0 || maskPtr == null || rawValues == null)
                    return;
                // Only the set bits carry values; count them so the span is exactly the packed values.
                int setBits = 0;
                for (int i = 0; i < maskLen; i++)
                    setBits += System.Numerics.BitOperations.PopCount(maskPtr[i]);
                if (RawValuators.TryExtractXY(new ReadOnlySpan<byte>(maskPtr, maskLen), new ReadOnlySpan<double>(rawValues, setBits), out var dx, out var dy)
                    && (dx != 0 || dy != 0))
                    _onRawDelta(dx, dy);
            }
            finally
            {
                XFreeEventData(_display, ev);
            }
        }
        else if (type == ButtonPress || type == ButtonRelease)
        {
            // Core button events arrive here only while our grab is active (they're routed to the
            // grabbing client). XButtonEvent.button @84.
            if (_active)
                _onButton((int)*(uint*)(ev + 84), type == ButtonPress);
        }
    }

    private void DoLock(IntPtr window)
    {
        if (_active)
            return;

        // Hide first: XFIXES hides per screen, so root is a window that can never go stale.
        XFixesHideCursor(_display, _root);
        XFlush(_display);

        // The middle-click that asked for the lock may still be held, and the toolkit's implicit grab
        // for that press blocks ours (AlreadyGrabbed) until the button comes up — so retry briefly.
        int result = -1;
        byte* ev = stackalloc byte[XEventSize];
        for (int attempt = 0; attempt < 100; attempt++)
        {
            result = XGrabPointer(_display, window, 0,
                ButtonPressMask | ButtonReleaseMask | PointerMotionMask,
                GrabModeAsync, GrabModeAsync, window, IntPtr.Zero, CurrentTime);
            if (result == GrabSuccess)
                break;
            // Keep raw/wake traffic moving while we wait for the grab.
            PollFds(XConnectionNumber(_display), 10);
            DrainWake();
            while (XPending(_display) > 0) { XNextEvent(_display, ev); HandleEvent(ev); }
        }
        if (result != GrabSuccess)
        {
            XFixesShowCursor(_display, _root);
            XFlush(_display);
            _log($"mouse lock: XGrabPointer failed ({GrabName(result)}) — using bounded pointer deltas this time");
            _onStateChanged(LockState.Refused);
            return;
        }

        _lockedWindow = window;
        WarpToCentre(window);
        XSync(_display, 0);
        _active = true;
        _onStateChanged(LockState.Engaged);
    }

    private void DoUnlock()
    {
        if (!_active)
            return;
        _active = false;
        WarpToCentre(_lockedWindow); // park the (still confined) pointer where the game's cursor was
        XUngrabPointer(_display, CurrentTime);
        XFixesShowCursor(_display, _root);
        XSync(_display, 0);
        _lockedWindow = IntPtr.Zero;
        _onStateChanged(LockState.Released);
    }

    private void WarpToCentre(IntPtr window)
    {
        if (XGetGeometry(_display, window, out _, out _, out _, out uint w, out uint h, out _, out _) != 0 && w > 0 && h > 0)
            XWarpPointer(_display, IntPtr.Zero, window, 0, 0, 0, 0, (int)(w / 2), (int)(h / 2));
    }

    private static string GrabName(int r) => r switch
    {
        AlreadyGrabbed => "AlreadyGrabbed",
        GrabInvalidTime => "GrabInvalidTime",
        GrabNotViewable => "GrabNotViewable",
        GrabFrozen => "GrabFrozen",
        _ => r.ToString(),
    };

    // ── poll() plumbing ──────────────────────────────────────────────────────────────────────────
    private void PollFds(int xfd, int timeoutMs)
    {
        var fds = stackalloc PollFd[2];
        fds[0] = new PollFd { Fd = xfd, Events = POLLIN };
        fds[1] = new PollFd { Fd = _wakeRead, Events = POLLIN };
        poll(fds, _wakeRead >= 0 ? 2u : 1u, timeoutMs);
    }

    private void WaitForWake(int timeoutMs)
    {
        if (_wakeRead < 0) { Thread.Sleep(timeoutMs < 0 ? 50 : timeoutMs); return; }
        var fds = stackalloc PollFd[1];
        fds[0] = new PollFd { Fd = _wakeRead, Events = POLLIN };
        poll(fds, 1, timeoutMs);
        DrainWake();
    }

    private void DrainWake()
    {
        if (_wakeRead < 0) return;
        byte* buf = stackalloc byte[64];
        var fds = stackalloc PollFd[1];
        fds[0] = new PollFd { Fd = _wakeRead, Events = POLLIN };
        while (poll(fds, 1, 0) > 0 && (fds[0].REvents & POLLIN) != 0)
            if (read(_wakeRead, buf, 64) <= 0) break;
    }

    // ── Interop (layouts verified against the C headers on x86-64) ───────────────────────────────
    private const int XEventSize = 192;
    private const int GenericEvent = 35, ButtonPress = 4, ButtonRelease = 5;
    private const int XI_RawMotion = 17, XIAllMasterDevices = 1, XIPointerEmulated = 1 << 16;
    private const uint ButtonPressMask = 1 << 2, ButtonReleaseMask = 1 << 3, PointerMotionMask = 1 << 6;
    private const int GrabModeAsync = 1;
    private const int GrabSuccess = 0, AlreadyGrabbed = 1, GrabInvalidTime = 2, GrabNotViewable = 3, GrabFrozen = 4;
    private const short POLLIN = 1;
    private static readonly nuint CurrentTime = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct XIEventMask { public int DeviceId; public int MaskLen; public byte* Mask; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { public int Fd; public short Events; public short REvents; }

    private const string LibX11 = "libX11.so.6", LibXi = "libXi.so.6", LibXfixes = "libXfixes.so.3", LibC = "libc";

    [DllImport(LibX11)] private static extern int XInitThreads();
    [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(string? name);
    [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(LibX11)] private static extern int XConnectionNumber(IntPtr display);
    [DllImport(LibX11)] private static extern int XPending(IntPtr display);
    [DllImport(LibX11)] private static extern int XNextEvent(IntPtr display, byte* ev);
    [DllImport(LibX11)] private static extern int XFlush(IntPtr display);
    [DllImport(LibX11)] private static extern int XSync(IntPtr display, int discard);
    [DllImport(LibX11)] private static extern int XQueryExtension(IntPtr display, string name, out int opcode, out int eventBase, out int errorBase);
    [DllImport(LibX11)] private static extern int XGetEventData(IntPtr display, byte* cookie);
    [DllImport(LibX11)] private static extern void XFreeEventData(IntPtr display, byte* cookie);
    [DllImport(LibX11)] private static extern int XGrabPointer(IntPtr display, IntPtr grabWindow, int ownerEvents, uint eventMask,
        int pointerMode, int keyboardMode, IntPtr confineTo, IntPtr cursor, nuint time);
    [DllImport(LibX11)] private static extern int XUngrabPointer(IntPtr display, nuint time);
    [DllImport(LibX11)] private static extern int XWarpPointer(IntPtr display, IntPtr srcWin, IntPtr destWin,
        int srcX, int srcY, uint srcWidth, uint srcHeight, int destX, int destY);
    [DllImport(LibX11)] private static extern int XGetGeometry(IntPtr display, IntPtr drawable, out IntPtr root,
        out int x, out int y, out uint width, out uint height, out uint borderWidth, out uint depth);
    [DllImport(LibXi)] private static extern int XIQueryVersion(IntPtr display, ref int major, ref int minor);
    [DllImport(LibXi)] private static extern int XISelectEvents(IntPtr display, IntPtr window, XIEventMask* masks, int numMasks);
    [DllImport(LibXfixes)] private static extern int XFixesQueryExtension(IntPtr display, out int eventBase, out int errorBase);
    [DllImport(LibXfixes)] private static extern int XFixesQueryVersion(IntPtr display, ref int major, ref int minor);
    [DllImport(LibXfixes)] private static extern void XFixesHideCursor(IntPtr display, IntPtr window);
    [DllImport(LibXfixes)] private static extern void XFixesShowCursor(IntPtr display, IntPtr window);
    [DllImport(LibC)] private static extern int poll(PollFd* fds, nuint nfds, int timeout);
    [DllImport(LibC)] private static extern int pipe(int* fds);
    [DllImport(LibC)] private static extern nint read(int fd, byte* buf, nuint count);
    [DllImport(LibC)] private static extern nint write(int fd, byte* buf, nuint count);
    [DllImport(LibC)] private static extern int close(int fd);
}
