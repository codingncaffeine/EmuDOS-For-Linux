using System.Runtime.InteropServices;

namespace EmuDOS.Core.Input;

/// <summary>
/// Loads SDL3 once for everything that uses it — game audio (EmuDOS's <c>SdlAudio</c>, through the
/// <c>SDL3</c> import resolved here) and gamepads (<see cref="Sdl3Controller"/>,
/// <see cref="Sdl3Gamepads"/>). Release builds ship <c>libSDL3.so.0</c> next to the binary (Debian
/// and Ubuntu LTS have no SDL3 package, and their runtime package lacks the unversioned
/// <c>libSDL3.so</c> name a plain <c>DllImport("SDL3")</c> would look for); a system SDL3 is the
/// fallback. Returns <see cref="IntPtr.Zero"/> if SDL3 isn't present, so callers degrade gracefully.
/// </summary>
public static class Sdl3Library
{
    private const uint InitGamepad = 0x00002000; // SDL_INIT_GAMEPAD

    private delegate byte InitDelegate(uint flags); // SDL_Init → bool (1 = success)

    private static readonly object Gate = new();
    private static IntPtr _lib;
    private static bool _loadAttempted;
    private static bool _gamepadAttempted;
    private static IntPtr _gamepadLib;

    /// <summary>Where SDL3 was loaded from (a path, or the soname the system loader resolved), or null.</summary>
    public static string? LoadedFrom { get; private set; }

    /// <summary>The loaded SDL3 library (IntPtr.Zero if unavailable). Loads exactly once.</summary>
    public static IntPtr Load(string? coresDir)
    {
        lock (Gate)
        {
            if (_loadAttempted)
                return _lib;
            _loadAttempted = true;

            foreach (var candidate in Candidates(coresDir))
            {
                // Absolute candidates (bundled) must exist; bare sonames are resolved by the loader.
                bool isPath = candidate.Contains(Path.DirectorySeparatorChar);
                if (isPath && !File.Exists(candidate))
                    continue;
                if (NativeLibrary.TryLoad(candidate, out var handle))
                {
                    _lib = handle;
                    LoadedFrom = candidate;
                    break;
                }
            }
            return _lib;
        }
    }

    /// <summary>SDL3 with its gamepad subsystem initialised (IntPtr.Zero if unavailable). Inits once.</summary>
    public static IntPtr Handle(string? coresDir)
    {
        var lib = Load(coresDir);
        lock (Gate)
        {
            if (_gamepadAttempted)
                return _gamepadLib;
            _gamepadAttempted = true;
            var init = Bind<InitDelegate>(lib, "SDL_Init");
            _gamepadLib = init is not null && init(InitGamepad) != 0 ? lib : IntPtr.Zero;
            return _gamepadLib;
        }
    }

    /// <summary>
    /// Route <c>[DllImport("SDL3")]</c> in <paramref name="assembly"/> through <see cref="Load"/>. A
    /// resolver can be set once per assembly; other library names keep the default resolution.
    /// </summary>
    public static void ResolveImportsFor(System.Reflection.Assembly assembly, string? coresDir) =>
        NativeLibrary.SetDllImportResolver(assembly, (name, _, _) =>
            name == "SDL3" ? Load(coresDir) : IntPtr.Zero);

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
        // The copy shipped next to the binary wins over the system one.
        yield return Path.Combine(AppContext.BaseDirectory, "libSDL3.so.0");
        yield return Path.Combine(AppContext.BaseDirectory, "libSDL3.so");
        yield return "libSDL3.so.0"; // Linux system package (versioned soname)
        yield return "libSDL3.so";
        yield return "SDL3";         // generic / NativeLibrary platform resolution
        yield return "SDL3.dll";
    }
}
