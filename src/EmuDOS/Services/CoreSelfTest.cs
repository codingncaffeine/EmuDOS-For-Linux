using System;
using System.IO;
using EmuDOS.Core.Libretro;

namespace EmuDOS.Services;

/// <summary>
/// Headless validation of the libretro host on Linux: load the dosbox_pure <c>.so</c>, bind every
/// retro_* export, run retro_init / retro_load_game, and pump retro_run to confirm the core produces
/// video frames through the software path — all without a window. Invoked via
/// <c>EmuDOS --selftest-core &lt;core.so&gt;</c>. Proves the NativeLibrary (dlopen) port + the
/// x64 marshaling before the presentation layer is built.
/// </summary>
public static class CoreSelfTest
{
    public static int Run(string corePath)
    {
        try
        {
            Console.WriteLine($"[core-selftest] loading {corePath}");
            using var core = new LibretroCore(corePath);

            int frames = 0, audioSamples = 0, fw = 0, fh = 0;
            core.Video = (_, w, h, _, _) => { frames++; if (fw == 0) { fw = w; fh = h; } };
            core.Audio = pcm => audioSamples += pcm.Length / 2;
            core.Input = (_, _, _, _) => 0;
            core.CoreLog = (_, _) => { };

            var sys = Directory.CreateTempSubdirectory("emudos-selftest-sys");
            core.SystemDirectory = sys.FullName;
            core.SaveDirectory = sys.FullName;

            core.SetCallbacks();
            core.Init();
            Console.WriteLine($"[core-selftest] retro_init OK — all exports bound, NeedsFullPath={core.NeedsFullPath}");

            // An empty content folder boots dosbox_pure to its start menu, which still renders frames —
            // enough to validate the video path end to end.
            var content = Directory.CreateTempSubdirectory("emudos-selftest-content");
            if (!core.LoadGame(content.FullName))
            {
                Console.WriteLine("=== FAIL: retro_load_game returned false ===");
                return 1;
            }

            var av = core.GetAvInfo();
            Console.WriteLine($"[core-selftest] loaded — av {av.BaseWidth}x{av.BaseHeight} @ {av.Fps:F2}fps, {av.SampleRate}Hz");

            for (int i = 0; i < 300; i++)
                core.Run();

            Console.WriteLine($"[core-selftest] ran 300 frames — video_refresh={frames}, first {fw}x{fh}, audioSamples={audioSamples}");
            bool ok = frames > 0;
            Console.WriteLine(ok ? "=== PASS ===" : "=== FAIL: core produced no frames ===");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"=== FAIL: {ex} ===");
            return 1;
        }
    }
}
