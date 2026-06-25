using System;
using System.Runtime.InteropServices;

namespace EmuDOS.Platform;

/// <summary>
/// X11 pointer warping for true in-game mouse lock. When the game grabs the mouse we hide the cursor
/// and, after every motion event, warp it back to the window centre — so the game receives unbounded
/// relative motion instead of the cursor clamping at the screen edge (the "can't keep turning" bug).
/// Best-effort: a no-op if the X11 display can't be opened (e.g. a pure-Wayland session with no Xwayland).
/// </summary>
internal static class X11Pointer
{
    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string? name);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")]
    private static extern int XWarpPointer(IntPtr display, IntPtr srcWin, IntPtr destWin,
        int srcX, int srcY, uint srcWidth, uint srcHeight, int destX, int destY);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);

    private static IntPtr _display;
    private static bool _tried;

    /// <summary>True if an X11 display is available, so warping will take effect.</summary>
    public static bool Available
    {
        get
        {
            if (!_tried)
            {
                _tried = true;
                try { _display = XOpenDisplay(null); }
                catch { _display = IntPtr.Zero; }
            }
            return _display != IntPtr.Zero;
        }
    }

    /// <summary>Warp the pointer to an absolute screen-pixel position (no-op if X11 is unavailable).</summary>
    public static void WarpTo(int screenX, int screenY)
    {
        if (!Available)
            return;
        try
        {
            var root = XDefaultRootWindow(_display);
            XWarpPointer(_display, IntPtr.Zero, root, 0, 0, 0, 0, screenX, screenY);
            XFlush(_display);
        }
        catch { /* best effort */ }
    }
}
