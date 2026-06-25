using System.Threading;

namespace EmuDOS.Core.Input;

/// <summary>
/// Watches for controllers being plugged in or removed and raises <see cref="Connected"/> /
/// <see cref="Disconnected"/> with the controller's friendly name (Xbox, DualSense, 8BitDo, …) for the
/// bottom status bar. Polls on a background timer, so it never touches the UI thread.
///
/// Detection is by the SET of connected controller names from SDL3 (a lightweight read that never opens
/// or closes pads), so it's immune to enumeration-order shuffles. A one-tick debounce absorbs the brief
/// drop/re-add flaps a Bluetooth pad produces while pairing, so connecting never flashes "disconnected".
/// Where SDL3 isn't available (e.g. Windows without the bundled SDL3.dll) it falls back to per-port
/// XInput. Controllers already connected at startup are recorded silently (no announcement spam).
/// </summary>
public sealed class ControllerMonitor : IDisposable
{
    private readonly IGamepadInput _input;
    private readonly Sdl3Gamepads _sdl;

    // SDL3 name-set path (preferred): the committed steady state, plus a pending candidate that must
    // survive one more tick before we trust it (the debounce).
    private List<string> _committed = new();
    private List<string>? _pending;

    // XInput fallback path (no SDL3): per-port connection flags.
    private readonly bool[] _wasConnected = new bool[4];

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

    private bool UseSdl => _sdl.Available;

    public void Start()
    {
        if (UseSdl)
        {
            _committed = CurrentNames(); // prime: what's already connected, announced silently
        }
        else
        {
            if (!_input.Available)
                return;
            _input.Poll();
            for (int p = 0; p < 4; p++)
                _wasConnected[p] = _input.IsConnected(p);
        }
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    private void Tick()
    {
        try
        {
            if (UseSdl)
                TickSdl();
            else
                TickXInput();
        }
        catch { /* polling is best-effort */ }
    }

    private void TickSdl()
    {
        var current = CurrentNames();

        if (Same(current, _committed))
        {
            _pending = null; // back to (or still at) the steady state — cancel any pending change
            return;
        }
        // A change must hold for two consecutive ticks before we announce it, so a Bluetooth pad's
        // momentary drop/re-add during pairing doesn't surface as connect→disconnect churn.
        if (_pending is not null && Same(current, _pending))
        {
            AnnounceDiff(_committed, current);
            _committed = current;
            _pending = null;
        }
        else
        {
            _pending = current;
        }
    }

    private void TickXInput()
    {
        _input.Poll();
        for (int p = 0; p < 4; p++)
        {
            bool now = _input.IsConnected(p);
            if (now == _wasConnected[p])
                continue;
            _wasConnected[p] = now;
            if (now)
                Connected?.Invoke("Controller");
            else
                Disconnected?.Invoke("Controller");
        }
    }

    private List<string> CurrentNames()
    {
        var names = _sdl.ConnectedNames();
        names.Sort(StringComparer.Ordinal); // order-independent comparison (handles duplicates too)
        return names;
    }

    private static bool Same(List<string> a, List<string> b) => a.SequenceEqual(b);

    // Multiset diff: names gained since the committed state are "connected", names lost are "disconnected".
    private void AnnounceDiff(List<string> before, List<string> after)
    {
        var remaining = new List<string>(before);
        foreach (var name in after)
        {
            if (!remaining.Remove(name))
                Connected?.Invoke(name);
        }
        foreach (var name in remaining)
            Disconnected?.Invoke(name);
    }

    public void Dispose() => _timer?.Dispose();
}
