namespace EmuDOS.Core.Input;

/// <summary>Picks the controller backend for the current OS: XInput where it's present (Windows),
/// otherwise SDL3 (Linux, and as a cross-platform fallback).</summary>
public static class GamepadInput
{
    public static IGamepadInput Create(string? coresDir = null)
    {
        var xinput = new XInputController();
        if (xinput.Available)
            return xinput;

        var sdl = new Sdl3Controller(coresDir);
        if (sdl.Available)
            return sdl;

        return xinput; // neither backend loaded — a harmless no-op (Available == false)
    }
}
