using System;
using System.IO;
using EmuDOS.Core.Engine.DosBoxPure;
using EmuDOS.Core.Import;
using EmuDOS.Core.Libretro;
using EmuDOS.Core.Model;

namespace EmuDOS.Services;

/// <summary>
/// The child side of a trial boot: <c>EmuDOS --trial-boot &lt;core.so&gt; &lt;content&gt; &lt;program&gt;</c>
/// loads the content headlessly (no window, no audio) with a batch that runs the program and then
/// writes a marker, pumps a few seconds of emulated time, and exits 0 when the program was still
/// running, 3 when it returned to DOS, 1 when the trial could not run. A process of its own, so it
/// never shares the core with a game running in the app.
/// </summary>
public static class TrialBootHost
{
    public const int ExitStillRunning = 0;
    public const int ExitReturnedToDos = 3;
    public const int ExitFailed = 1;

    // ~4.3 s of emulated time at dosbox_pure's 70 Hz; boot takes well under a second of it.
    private const int Frames = 300;

    public static int Run(string corePath, string contentDir, string executable)
    {
        var marker = Path.Combine(contentDir, TrialBootScript.MarkerFile);
        var scratch = Directory.CreateTempSubdirectory("emudos-trial");
        try
        {
            File.Delete(marker);
            File.WriteAllText(Path.Combine(contentDir, "DOSBOX.BAT"), TrialBootScript.Build(executable));

            using var core = new LibretroCore(corePath)
            {
                SystemDirectory = scratch.FullName,
                SaveDirectory = scratch.FullName,
            };
            core.Video = (_, _, _, _, _) => { };
            core.Audio = _ => { };
            core.Input = (_, _, _, _) => 0;
            core.CoreLog = (_, _) => { };
            core.Options = DosBoxPureAdapter.BuildCoreOptions(new GameProfile(), contentDir);
            core.SetCallbacks();
            core.Init();
            if (!core.LoadGame(contentDir))
                return ExitFailed;

            for (int frame = 0; frame < Frames; frame++)
            {
                core.Run();
                if (frame % 10 == 9 && File.Exists(marker))
                    return ExitReturnedToDos;
            }
            return File.Exists(marker) ? ExitReturnedToDos : ExitStillRunning;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"trial boot failed: {ex.Message}");
            return ExitFailed;
        }
        finally
        {
            try { File.Delete(marker); } catch { }
            try { scratch.Delete(recursive: true); } catch { }
        }
    }
}
