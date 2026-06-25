using System.Runtime.InteropServices;

namespace EmuDOS.Core.Input;

/// <summary>
/// Identifies connected game controllers by friendly name via SDL3 (Xbox, DualSense, 8BitDo, …).
/// Input itself is read through <see cref="Sdl3Controller"/> / <see cref="XInputController"/>; this is
/// purely for recognition/display. SDL3 is optional (a system package on Linux, a bundled SDL3.dll on
/// Windows) — if it isn't present, <see cref="Available"/> is false and the app just shows generic
/// controller status.
/// </summary>
public sealed class Sdl3Gamepads
{
    private delegate IntPtr GetGamepadsDelegate(out int count); // SDL_GetGamepads → SDL_JoystickID* (free it)
    private delegate IntPtr GetNameForIdDelegate(uint id);      // SDL_GetGamepadNameForID → const char* (UTF-8)
    private delegate void FreeDelegate(IntPtr mem);             // SDL_free
    private delegate void UpdateDelegate();                     // SDL_UpdateGamepads (refresh hot-plug list)

    private readonly GetGamepadsDelegate? _getGamepads;
    private readonly GetNameForIdDelegate? _getName;
    private readonly FreeDelegate? _free;
    private readonly UpdateDelegate? _update;
    private readonly bool _ready;

    public Sdl3Gamepads(string coresDir)
    {
        var lib = Sdl3Library.Handle(coresDir);
        if (lib == IntPtr.Zero)
            return;

        _getGamepads = Sdl3Library.Bind<GetGamepadsDelegate>(lib, "SDL_GetGamepads");
        _getName = Sdl3Library.Bind<GetNameForIdDelegate>(lib, "SDL_GetGamepadNameForID");
        _free = Sdl3Library.Bind<FreeDelegate>(lib, "SDL_free");
        _update = Sdl3Library.Bind<UpdateDelegate>(lib, "SDL_UpdateGamepads");

        _ready = _getGamepads is not null && _getName is not null && _free is not null;
    }

    /// <summary>True when SDL3 loaded and initialized (i.e. recognition is available).</summary>
    public bool Available => _ready;

    /// <summary>Friendly names of the controllers connected right now. Empty if SDL3 is absent.</summary>
    public List<string> ConnectedNames()
    {
        var names = new List<string>();
        if (!_ready || _getGamepads is null || _getName is null || _free is null)
            return names;
        try
        {
            _update?.Invoke(); // refresh the device list so freshly-plugged pads appear
            var ptr = _getGamepads(out int count);
            if (ptr == IntPtr.Zero)
                return names;
            for (int i = 0; i < count; i++)
            {
                var id = (uint)Marshal.ReadInt32(ptr, i * sizeof(uint));
                var namePtr = _getName(id);
                var name = namePtr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(namePtr);
                names.Add(string.IsNullOrWhiteSpace(name) ? "Controller" : name!);
            }
            _free(ptr);
        }
        catch
        {
            // SDL hiccup — return whatever we gathered.
        }
        return names;
    }
}
