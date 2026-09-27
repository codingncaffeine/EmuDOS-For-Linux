using EmuDOS.Core.Catalog;
using EmuDOS.Core.Import;
using EmuDOS.Core.Infrastructure;
using EmuDOS.Core.Library;
using EmuDOS.Core.Model;

namespace EmuDOS.Tests;

public class ProfileResolverTests
{
    [Fact]
    public void Enriches_baseline_when_catalog_matches_but_keeps_identity()
    {
        var resolver = new ProfileResolver(Catalog(AlphaEntry()));
        var baseline = new GameProfile
        {
            Title = "My Alpha",
            Launch = new LaunchSpec { Executable = "ALPHA.EXE" },
        };

        var result = resolver.Resolve(baseline, ["alpha.exe", "alpha.dat"]);

        Assert.True(result.Recognized);
        Assert.Equal(ProfileOrigin.CuratedBase, result.Profile.Origin);
        Assert.Equal(20000, result.Profile.Cpu.FixedCycles); // curated config applied
        Assert.Equal("My Alpha", result.Profile.Title);        // gamebox identity preserved
    }

    [Fact]
    public void Returns_baseline_unchanged_when_no_match()
    {
        var resolver = new ProfileResolver(Catalog(AlphaEntry()));
        var baseline = new GameProfile { Title = "Unknown" };

        var result = resolver.Resolve(baseline, ["mystery.exe"]);

        Assert.False(result.Recognized);
        Assert.Same(baseline, result.Profile);
        Assert.Null(result.Executable);
    }

    [Fact]
    public void Never_clobbers_a_user_override()
    {
        var resolver = new ProfileResolver(Catalog(AlphaEntry()));
        var baseline = new GameProfile
        {
            Title = "My Alpha",
            Origin = ProfileOrigin.UserOverride,
            Cpu = new CpuSpec { CyclesMode = CyclesMode.Fixed, FixedCycles = 999 },
        };

        var result = resolver.Resolve(baseline, ["alpha.exe", "alpha.dat"]);

        Assert.Same(baseline, result.Profile);
        Assert.Equal(999, result.Profile.Cpu.FixedCycles);
    }

    [Fact]
    public void The_catalog_program_replaces_the_guess_when_the_content_has_it()
    {
        var resolver = new ProfileResolver(Catalog(AlphaEntry() with { Launch = ["ALPHAGO.BAT", "ALPHA.EXE"] }));
        var baseline = new GameProfile
        {
            Title = "Alpha",
            Launch = new LaunchSpec { Executable = @"ALPHA\ALPHAED.EXE", PreCommands = ["IMGMOUNT D: \"c:\\gamecd.cue\" -t cdrom"] },
        };

        var result = resolver.Resolve(baseline, [@"ALPHA\ALPHA.EXE", @"ALPHA\ALPHAED.EXE", @"ALPHA\ALPHA.DAT"]);

        Assert.Equal(@"ALPHA\ALPHA.EXE", result.Executable);
        Assert.Equal(@"ALPHA\ALPHA.EXE", result.Profile.Launch.Executable);
        Assert.Single(result.Profile.Launch.PreCommands); // the gamebox's own mount recipe is kept
    }

    [Fact]
    public void The_guess_stays_when_the_content_lacks_the_catalog_program()
    {
        var resolver = new ProfileResolver(Catalog(AlphaEntry() with { Launch = ["ALPHA.EXE"] }));
        var baseline = new GameProfile { Title = "Alpha", Launch = new LaunchSpec { Executable = "INSTALL.EXE" } };

        // Recognised by title (an installer-only copy has none of the telltales).
        var result = resolver.Resolve(baseline, ["INSTALL.EXE", "ALPHA.1"]);

        Assert.True(result.Recognized);
        Assert.Null(result.Executable);
        Assert.Equal("INSTALL.EXE", result.Profile.Launch.Executable);
    }

    [Fact]
    public void LaunchExecutable_reads_a_content_folder()
    {
        var resolver = new ProfileResolver(Catalog(AlphaEntry() with { Launch = ["ALPHA.EXE"] }));
        var content = Path.Combine(TempRoot(), "content");
        Directory.CreateDirectory(Path.Combine(content, "GAME"));
        File.WriteAllText(Path.Combine(content, "GAME", "ALPHA.EXE"), "x");
        File.WriteAllText(Path.Combine(content, "GAME", "ALPHA.DAT"), "x");
        File.WriteAllText(Path.Combine(content, "GAME", "OTHER.EXE"), "x");

        Assert.Equal(@"GAME\ALPHA.EXE", resolver.LaunchExecutable(content, "whatever"));
        Assert.Null(resolver.LaunchExecutable(Path.Combine(TempRoot(), "missing"), "Alpha"));
    }

    [Fact]
    public async Task Import_with_resolver_applies_curated_config_and_program()
    {
        var paths = new AppPaths(TempRoot());
        var store = new GameboxStore();
        var catalog = new CatalogDatabase(Path.Combine(paths.DataRoot, "catalog.db"));
        catalog.Build([new CatalogEntry
        {
            Id = "alpha",
            Title = "Alpha Strike",
            Telltales = ["alpha.exe"],
            Launch = ["ALPHA.EXE"],
            Profile = new GameProfile
            {
                Title = "Alpha Strike",
                Cpu = new CpuSpec { CyclesMode = CyclesMode.Fixed, FixedCycles = 20000 },
            },
        }]);
        var pipeline = new ImportPipeline(paths, store, new ProfileResolver(catalog));

        // A folder whose heuristic pick would be the title-named editor, not the game.
        var source = Path.Combine(TempRoot(), "Striker");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "ALPHA.EXE"), "x");
        File.WriteAllText(Path.Combine(source, "STRIKED.EXE"), "x");

        var result = await pipeline.ImportAsync(source);

        Assert.True(result.Success, result.Error);
        Assert.True(result.CatalogRecognized);
        Assert.Equal(LaunchSource.Catalog, result.LaunchSource);
        Assert.Equal("ALPHA.EXE", result.ChosenExecutable);
        var profile = store.ReadProfile(result.GameboxPath!);
        Assert.Equal(ProfileOrigin.CuratedBase, profile.Origin);
        Assert.Equal(20000, profile.Cpu.FixedCycles);
        Assert.Equal("ALPHA.EXE", profile.Launch.Executable);
    }

    [Fact]
    public async Task Import_without_a_catalog_hit_reports_the_guess()
    {
        var paths = new AppPaths(TempRoot());
        var pipeline = new ImportPipeline(paths, new GameboxStore(), new ProfileResolver(Catalog(AlphaEntry())));
        var source = Path.Combine(TempRoot(), "Striker");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "STRIKER.EXE"), "x");

        var result = await pipeline.ImportAsync(source);

        Assert.True(result.Success, result.Error);
        Assert.False(result.CatalogRecognized);
        Assert.Equal(LaunchSource.Guess, result.LaunchSource);
        Assert.Equal("STRIKER.EXE", result.ChosenExecutable);
    }

    private static CatalogDatabase Catalog(params CatalogEntry[] entries)
    {
        var db = new CatalogDatabase(Path.Combine(TempRoot(), "catalog.db"));
        db.Build(entries);
        return db;
    }

    private static CatalogEntry AlphaEntry() => new()
    {
        Id = "alpha",
        Title = "Alpha",
        Telltales = ["alpha.exe", "alpha.dat"],
        Profile = new GameProfile
        {
            Title = "Alpha",
            Origin = ProfileOrigin.CuratedBase,
            Cpu = new CpuSpec { CyclesMode = CyclesMode.Fixed, FixedCycles = 20000 },
        },
    };

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "emudos-tests", Guid.NewGuid().ToString("N"));
}
