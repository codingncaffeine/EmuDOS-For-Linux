namespace EmuDOS.Core.Input;

/// <summary>
/// A polled game-controller backend that maps physical pads to <see cref="PadButton"/> bits. XInput
/// on Windows, SDL3 on Linux (and as a cross-platform fallback). <see cref="Poll"/> latches a
/// per-port snapshot once per frame on the emulation thread; <see cref="IsButtonDown"/> reads it.
/// </summary>
public interface IGamepadInput
{
    /// <summary>True if a controller backend loaded (otherwise every query returns false/disconnected).</summary>
    bool Available { get; }

    /// <summary>Refresh the per-port button snapshot. Call once per frame on the emulation thread.</summary>
    void Poll();

    /// <summary>Whether <paramref name="button"/> is held on <paramref name="port"/> (0–3) as of the last Poll.</summary>
    bool IsButtonDown(int port, PadButton button);

    /// <summary>Whether a controller is connected on this port (refreshed by <see cref="Poll"/>).</summary>
    bool IsConnected(int port);
}
