using EmuDOS.Core.Input;

namespace EmuDOS.Tests;

public class GamepadInputTests
{
    [Fact]
    public void Create_returns_a_backend_that_polls_without_throwing()
    {
        var pad = GamepadInput.Create();

        // Must be safe to poll every frame and query even with no controller attached / no backend.
        // (Don't assert port 0 is empty — a controller may genuinely be plugged into the test machine.)
        pad.Poll();
        pad.Poll();

        _ = pad.IsButtonDown(0, PadButton.A); // returns a bool without throwing, controller or not
        _ = pad.IsConnected(0);
        Assert.False(pad.IsButtonDown(-1, PadButton.Start)); // out-of-range ports are always safe/false
        Assert.False(pad.IsButtonDown(99, PadButton.Start));
    }

    [Fact]
    public void Sdl3_backend_initialises_when_the_library_is_present()
    {
        // Documents the Linux integration: where libSDL3 is installed, the SDL3 backend must come up.
        bool sdlPresent = File.Exists("/usr/lib/libSDL3.so.0") || File.Exists("/usr/lib/libSDL3.so")
                          || File.Exists("/usr/lib64/libSDL3.so.0");
        var pad = new Sdl3Controller(null);
        pad.Poll(); // never throws, controller or not

        if (sdlPresent)
            Assert.True(pad.Available, "libSDL3 is installed but the SDL3 gamepad backend failed to initialise.");
    }

    [Fact]
    public void Factory_backend_is_available_on_linux_with_sdl3()
    {
        // The status-bar ControllerMonitor only polls when its backend reports Available. Regression
        // guard: on Linux (no XInput) the factory must fall through to SDL3 and be Available where the
        // library is present — otherwise controller connect/disconnect is never announced. SDL3's
        // built-in database covers Xbox, DualSense, DualShock, Switch Pro, 8BitDo and many more.
        bool sdlPresent = File.Exists("/usr/lib/libSDL3.so.0") || File.Exists("/usr/lib/libSDL3.so")
                          || File.Exists("/usr/lib64/libSDL3.so.0");
        if (!sdlPresent || OperatingSystem.IsWindows())
            return; // nothing to assert without SDL3 / on the XInput platform

        var pad = GamepadInput.Create();
        Assert.True(pad.Available,
            "On Linux with SDL3 installed, GamepadInput.Create() must return an Available backend so the "
            + "controller monitor actually runs.");
    }

    [Fact]
    public void ControllerMonitor_start_and_dispose_are_safe()
    {
        // Constructs the real monitor (cross-platform backend) and exercises its lifecycle — this would
        // have been a silent no-op back when it was hardcoded to the Windows-only XInput on Linux.
        using var monitor = new ControllerMonitor(coresDir: null!);
        monitor.Start();
    }
}
