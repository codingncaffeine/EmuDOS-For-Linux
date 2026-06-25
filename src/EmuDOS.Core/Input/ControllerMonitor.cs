using System.Threading;

namespace EmuDOS.Core.Input;

/// <summary>
/// Watches for controllers being plugged in or removed and raises <see cref="Connected"/> /
/// <see cref="Disconnected"/> with the controller's friendly name (via SDL3 when available, else a
/// generic label). Polls the platform controller backend (XInput on Windows, SDL3 on Linux) on a
/// background timer, so it never touches the UI thread. Controllers already plugged in at startup are
/// recorded silently (no announcement spam on every launch).
/// </summary>
public sealed class ControllerMonitor : IDisposable
{
    private readonly IGamepadInput _input;
    private readonly Sdl3Gamepads _sdl;
    private readonly bool[] _wasConnected = new bool[4];
    private readonly string[] _names = new string[4];
    private Timer? _timer;

    /// <summary>Raised (off the UI thread) with the controller name when one is connected.</summary>
    public event Action<string>? Connected;

    /// <summary>Raised (off the UI thread) with the controller name when one is removed.</summary>
    public event Action<string>? Disconnected;

    public ControllerMonitor(string coresDir)
    {
        _input = GamepadInput.Create(coresDir);
        _sdl = new Sdl3Gamepads(coresDir);
    }

    public void Start()
    {
        if (!_input.Available)
            return;
        _input.Poll(); // prime: record what's already plugged in without announcing it
        for (int p = 0; p < 4; p++)
        {
            _wasConnected[p] = _input.IsConnected(p);
            if (_wasConnected[p])
                _names[p] = NameFor(p);
        }
        _timer = new Timer(_ => Tick(), null, 1500, 1500);
    }

    private void Tick()
    {
        try
        {
            _input.Poll();
            for (int p = 0; p < 4; p++)
            {
                bool now = _input.IsConnected(p);
                if (now == _wasConnected[p])
                    continue;
                _wasConnected[p] = now;
                if (now)
                {
                    _names[p] = NameFor(p);
                    Connected?.Invoke(_names[p]);
                }
                else
                {
                    var name = string.IsNullOrEmpty(_names[p]) ? "Controller" : _names[p];
                    _names[p] = string.Empty;
                    Disconnected?.Invoke(name);
                }
            }
        }
        catch { /* polling is best-effort */ }
    }

    // Best-effort friendly name for a port via SDL3 (e.g. "8BitDo Pro 2"). On Windows the XInput↔SDL
    // ordering isn't guaranteed, so for the common single-controller case this is exact; for several at
    // once it's approximate. On Linux the backend IS SDL3, so the ordering matches.
    private string NameFor(int port)
    {
        var names = _sdl.ConnectedNames();
        if (names.Count == 0)
            return "Controller";
        return port < names.Count ? names[port] : names[0];
    }

    public void Dispose() => _timer?.Dispose();
}
