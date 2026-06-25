using System.Runtime.InteropServices;

namespace EmuDOS.Core.Input;

/// <summary>
/// Reads game controllers via SDL3's gamepad API — the Linux (and cross-platform) counterpart to
/// <see cref="XInputController"/>. SDL maps any recognised pad (Xbox, DualSense, 8BitDo, …) onto its
/// standard layout, which we translate to <see cref="PadButton"/> bits. Up to four ports in SDL's
/// connection order; the left stick doubles as a d-pad and triggers act as L2/R2, mirroring XInput.
/// </summary>
public sealed class Sdl3Controller : IGamepadInput
{
    private delegate void UpdateDelegate();                       // SDL_UpdateGamepads
    private delegate IntPtr GetGamepadsDelegate(out int count);   // SDL_GetGamepads → SDL_JoystickID* (free it)
    private delegate IntPtr OpenDelegate(uint instanceId);        // SDL_OpenGamepad → SDL_Gamepad*
    private delegate void CloseDelegate(IntPtr gamepad);          // SDL_CloseGamepad
    private delegate void FreeDelegate(IntPtr mem);               // SDL_free
    private delegate byte GetButtonDelegate(IntPtr gamepad, int button); // SDL_GetGamepadButton → bool
    private delegate short GetAxisDelegate(IntPtr gamepad, int axis);    // SDL_GetGamepadAxis → Sint16

    private readonly UpdateDelegate? _update;
    private readonly GetGamepadsDelegate? _getGamepads;
    private readonly OpenDelegate? _open;
    private readonly CloseDelegate? _close;
    private readonly FreeDelegate? _free;
    private readonly GetButtonDelegate? _getButton;
    private readonly GetAxisDelegate? _getAxis;
    private readonly bool _ready;

    private readonly Dictionary<uint, IntPtr> _handles = new(); // open SDL_Gamepad* by instance id
    private readonly uint[] _snapshot = new uint[4];            // per-port PadButton bitmask, latched on Poll
    private readonly bool[] _connected = new bool[4];

    // SDL_GamepadButton ids (SDL3 standard layout).
    private const int South = 0, East = 1, West = 2, North = 3, Back = 4, Start = 6;
    private const int LeftStick = 7, RightStick = 8, LeftShoulder = 9, RightShoulder = 10;
    private const int DpadUp = 11, DpadDown = 12, DpadLeft = 13, DpadRight = 14;

    // SDL_GamepadAxis ids.
    private const int AxisLeftX = 0, AxisLeftY = 1, AxisLeftTrigger = 4, AxisRightTrigger = 5;

    private const short StickDeadzone = 8000;     // ~25% of 32767
    private const short TriggerThreshold = 8000;  // SDL triggers are 0..32767

    public Sdl3Controller(string? coresDir)
    {
        var lib = Sdl3Library.Handle(coresDir);
        if (lib == IntPtr.Zero)
            return;

        _update = Sdl3Library.Bind<UpdateDelegate>(lib, "SDL_UpdateGamepads");
        _getGamepads = Sdl3Library.Bind<GetGamepadsDelegate>(lib, "SDL_GetGamepads");
        _open = Sdl3Library.Bind<OpenDelegate>(lib, "SDL_OpenGamepad");
        _close = Sdl3Library.Bind<CloseDelegate>(lib, "SDL_CloseGamepad");
        _free = Sdl3Library.Bind<FreeDelegate>(lib, "SDL_free");
        _getButton = Sdl3Library.Bind<GetButtonDelegate>(lib, "SDL_GetGamepadButton");
        _getAxis = Sdl3Library.Bind<GetAxisDelegate>(lib, "SDL_GetGamepadAxis");

        _ready = _getGamepads is not null && _open is not null && _free is not null
                 && _getButton is not null && _getAxis is not null;
    }

    public bool Available => _ready;

    public void Poll()
    {
        if (!_ready)
            return;
        Array.Clear(_snapshot);
        Array.Clear(_connected);
        try
        {
            _update?.Invoke(); // refresh the device list so freshly-plugged pads appear
            var ptr = _getGamepads!(out int count);
            if (ptr == IntPtr.Zero)
            {
                CloseAllExcept(null);
                return;
            }

            var present = new HashSet<uint>();
            int port = 0;
            for (int i = 0; i < count; i++)
            {
                uint id = (uint)Marshal.ReadInt32(ptr, i * sizeof(uint));
                present.Add(id);

                if (!_handles.TryGetValue(id, out var pad))
                {
                    pad = _open!(id);
                    if (pad == IntPtr.Zero)
                        continue; // couldn't open (e.g. an unmapped joystick) — skip it
                    _handles[id] = pad;
                }

                if (port < 4)
                {
                    _connected[port] = true;
                    _snapshot[port] = Map(pad);
                    port++;
                }
            }
            _free!(ptr);
            CloseAllExcept(present); // release pads that were unplugged
        }
        catch
        {
            // SDL hiccup — leave the cleared snapshot (no buttons) for this frame.
        }
    }

    public bool IsButtonDown(int port, PadButton button) =>
        port is >= 0 and < 4 && (_snapshot[port] & (1u << (int)button)) != 0;

    public bool IsConnected(int port) => port is >= 0 and < 4 && _connected[port];

    private void CloseAllExcept(HashSet<uint>? present)
    {
        if (_close is null || _handles.Count == 0)
            return;
        foreach (var id in _handles.Keys.ToList())
            if (present is null || !present.Contains(id))
            {
                _close(_handles[id]);
                _handles.Remove(id);
            }
    }

    private uint Map(IntPtr pad)
    {
        uint bits = 0;
        void Set(PadButton b) => bits |= 1u << (int)b;
        bool Down(int sdlButton) => _getButton!(pad, sdlButton) != 0;

        // Face buttons — SDL South (Xbox A) is libretro B, SDL East (Xbox B) is libretro A, etc.
        if (Down(South)) Set(PadButton.B);
        if (Down(East)) Set(PadButton.A);
        if (Down(West)) Set(PadButton.Y);
        if (Down(North)) Set(PadButton.X);

        if (Down(Start)) Set(PadButton.Start);
        if (Down(Back)) Set(PadButton.Select);
        if (Down(LeftShoulder)) Set(PadButton.L);
        if (Down(RightShoulder)) Set(PadButton.R);
        if (Down(LeftStick)) Set(PadButton.L3);
        if (Down(RightStick)) Set(PadButton.R3);

        if (Down(DpadUp)) Set(PadButton.Up);
        if (Down(DpadDown)) Set(PadButton.Down);
        if (Down(DpadLeft)) Set(PadButton.Left);
        if (Down(DpadRight)) Set(PadButton.Right);

        if (_getAxis!(pad, AxisLeftTrigger) > TriggerThreshold) Set(PadButton.L2);
        if (_getAxis!(pad, AxisRightTrigger) > TriggerThreshold) Set(PadButton.R2);

        // Left stick doubles as the d-pad (dosbox_pure reads the joypad digitally). SDL's Y axis is
        // positive-down, so up is negative.
        short lx = _getAxis!(pad, AxisLeftX), ly = _getAxis!(pad, AxisLeftY);
        if (ly < -StickDeadzone) Set(PadButton.Up);
        if (ly > StickDeadzone) Set(PadButton.Down);
        if (lx < -StickDeadzone) Set(PadButton.Left);
        if (lx > StickDeadzone) Set(PadButton.Right);

        return bits;
    }
}
