using EmuDOS.Core.Import;
using EmuDOS.Core.Infrastructure;
using EmuDOS.Core.Library;

namespace EmuDOS.Tests;

public class TrialBootTests
{
    [Fact]
    public async Task A_guess_that_returns_to_dos_gives_way_to_the_next_candidate()
    {
        var booter = new FakeBooter { ["ZQBIG.EXE"] = TrialOutcome.ReturnedToDos, ["ZQRUN.EXE"] = TrialOutcome.StillRunning };
        var result = await Import(booter, ("ZQBIG.EXE", 90000), ("ZQRUN.EXE", 40000));

        Assert.Equal("ZQRUN.EXE", result.ChosenExecutable);
        Assert.Equal(["ZQBIG.EXE"], result.TrialRejected);
        Assert.Equal(["ZQBIG.EXE", "ZQRUN.EXE"], booter.Tried);
        Assert.Contains("ZQBIG.EXE went straight back to DOS, so it runs ZQRUN.EXE", ImportSummary.Describe(result));
    }

    [Fact]
    public async Task A_guess_that_keeps_running_stays()
    {
        var booter = new FakeBooter { ["ZQBIG.EXE"] = TrialOutcome.StillRunning };
        var result = await Import(booter, ("ZQBIG.EXE", 90000), ("ZQRUN.EXE", 40000));

        Assert.Equal("ZQBIG.EXE", result.ChosenExecutable);
        Assert.Empty(result.TrialRejected);
        Assert.Equal(["ZQBIG.EXE"], booter.Tried);
    }

    [Fact]
    public async Task A_strong_guess_is_not_tried()
    {
        var booter = new FakeBooter();
        var result = await Import(booter, ("ZETA.EXE", 9000), ("OTHER.EXE", 90000));

        Assert.Equal("ZETA.EXE", result.ChosenExecutable); // an exact title word
        Assert.Empty(booter.Tried);
    }

    [Fact]
    public async Task Content_with_a_disc_is_not_tried()
    {
        var booter = new FakeBooter { ["ZQBIG.EXE"] = TrialOutcome.ReturnedToDos };
        var result = await Import(booter, ("ZQBIG.EXE", 90000), ("ZQRUN.EXE", 40000), ("CD/ZQ.CUE", 60));

        Assert.Empty(booter.Tried);
        Assert.Empty(result.TrialRejected);
    }

    [Fact]
    public async Task No_core_keeps_the_guess()
    {
        var booter = new FakeBooter { ["ZQBIG.EXE"] = TrialOutcome.Unavailable };
        var result = await Import(booter, ("ZQBIG.EXE", 90000), ("ZQRUN.EXE", 40000));

        Assert.Equal("ZQBIG.EXE", result.ChosenExecutable);
        Assert.Empty(result.TrialRejected);
    }

    [Fact]
    public async Task When_everything_returns_to_dos_the_guess_stays()
    {
        var booter = new FakeBooter { ["ZQBIG.EXE"] = TrialOutcome.ReturnedToDos, ["ZQRUN.EXE"] = TrialOutcome.ReturnedToDos };
        var result = await Import(booter, ("ZQBIG.EXE", 90000), ("ZQRUN.EXE", 40000));

        Assert.Equal("ZQBIG.EXE", result.ChosenExecutable);
        Assert.Empty(result.TrialRejected);
    }

    [Fact]
    public void The_trial_batch_calls_batches_runs_from_the_folder_and_leaves_a_marker()
    {
        Assert.Equal("@ECHO OFF\r\n@CD \\GAME\r\n@CALL START.BAT\r\n@ECHO.>C:\\EMUTRY.TMP\r\n", TrialBootScript.Build(@"GAME\START.BAT"));
        Assert.Equal("@ECHO OFF\r\n@ZQ.EXE\r\n@ECHO.>C:\\EMUTRY.TMP\r\n", TrialBootScript.Build("ZQ.EXE"));
    }

    private static async Task<ImportResult> Import(ITrialBooter booter, params (string Path, int Size)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "emudos-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Zeta");
        foreach (var (path, size) in files)
        {
            var full = Path.Combine(source, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[size]);
        }
        var pipeline = new ImportPipeline(new AppPaths(Path.Combine(root, "data")), new GameboxStore(), trialBooter: booter);
        var result = await pipeline.ImportAsync(source);
        Assert.True(result.Success, result.Error);
        return result;
    }

    private sealed class FakeBooter : Dictionary<string, TrialOutcome>, ITrialBooter
    {
        public List<string> Tried { get; } = [];

        public Task<TrialOutcome> BootAsync(string contentDir, string executable, CancellationToken cancellationToken = default)
        {
            Tried.Add(executable);
            return Task.FromResult(TryGetValue(executable, out var outcome) ? outcome : TrialOutcome.StillRunning);
        }
    }
}
