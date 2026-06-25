using EmuDOS.Core.Input;

namespace EmuDOS.Tests;

public class GamepadInputTests
{
    [Fact]
    public void Create_returns_a_backend_that_polls_without_throwing()
    {
        var pad = GamepadInput.Create();

        // Must be safe to poll every frame and query even with no controller attached / no backend.
        pad.Poll();
        pad.Poll();

        Assert.False(pad.IsButtonDown(0, PadButton.A));
        Assert.False(pad.IsConnected(0));
        Assert.False(pad.IsButtonDown(-1, PadButton.Start)); // out-of-range ports are safe
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
}
