using System.Runtime.InteropServices;

namespace EmuDOS.Core.Input;

/// <summary>
/// Loads SDL3 once and initialises its gamepad subsystem, shared by the input reader
/// (<see cref="Sdl3Controller"/>) and the name display (<see cref="Sdl3Gamepads"/>). On Linux SDL3
/// is a system package (libSDL3.so); on Windows it can ship as a bundled SDL3.dll in the cores
/// folder. Returns <see cref="IntPtr.Zero"/> if SDL3 isn't present, so callers degrade gracefully.
/// </summary>
internal static class Sdl3Library
{
    private const uint InitGamepad = 0x00002000; // SDL_INIT_GAMEPAD

    private delegate byte InitDelegate(uint flags); // SDL_Init → bool (1 = success)

    private static readonly object Gate = new();
    private static IntPtr _lib;
    private static bool _attempted;

    /// <summary>The loaded SDL3 handle (IntPtr.Zero if unavailable). Loads + inits exactly once.</summary>
    public static IntPtr Handle(string? coresDir)
    {
        lock (Gate)
        {
            if (_attempted)
                return _lib;
            _attempted = true;

            foreach (var candidate in Candidates(coresDir))
            {
                // Absolute candidates (bundled) must exist; bare sonames are resolved by the loader.
                bool isPath = candidate.Contains(Path.DirectorySeparatorChar);
                if (isPath && !File.Exists(candidate))
                    continue;
                if (NativeLibrary.TryLoad(candidate, out var handle))
                {
                    _lib = handle;
                    break;
                }
            }

            if (_lib != IntPtr.Zero)
            {
                var init = Bind<InitDelegate>(_lib, "SDL_Init");
                if (init is null || init(InitGamepad) == 0)
                    _lib = IntPtr.Zero; // loaded but couldn't init the subsystem — treat as unavailable
            }
            return _lib;
        }
    }

    public static T? Bind<T>(IntPtr lib, string name) where T : Delegate =>
        lib != IntPtr.Zero && NativeLibrary.TryGetExport(lib, name, out var p)
            ? Marshal.GetDelegateForFunctionPointer<T>(p)
            : null;

    private static IEnumerable<string> Candidates(string? coresDir)
    {
        if (!string.IsNullOrEmpty(coresDir))
        {
            yield return Path.Combine(coresDir, "SDL3.dll");   // Windows bundled
            yield return Path.Combine(coresDir, "libSDL3.so"); // Linux bundled (rare)
        }
        yield return "libSDL3.so.0"; // Linux system package (versioned soname)
        yield return "libSDL3.so";
        yield return "SDL3";         // generic / NativeLibrary platform resolution
        yield return "SDL3.dll";
    }
}
