using System.Text;
using EmuDOS.Core.Catalog;
using EmuDOS.Core.Model;

namespace EmuDOS.Tests;

public class CatalogDatabaseTests
{
    [Fact]
    public void Matches_and_returns_curated_profile_when_all_telltales_present()
    {
        var db = NewCatalog();
        db.Build([Entry("alpha", "Alpha Strike", ["alpha.exe", "alpha.dat"], 20000), Entry("beta", "Beta Run", ["beta.exe"], 8000)]);

        var match = db.Match(["ALPHA.EXE", "alpha.dat", "setup.exe"]);

        Assert.NotNull(match);
        Assert.Equal(CatalogMatchKind.Files, match!.Kind);
        Assert.Equal(20000, match.Profile.Cpu.FixedCycles);
    }

    [Fact]
    public void Requires_every_telltale_to_be_present()
    {
        var db = NewCatalog();
        db.Build([Entry("alpha", "Alpha Strike", ["alpha.exe", "alphadat.dat"], 20000)]);

        Assert.Null(db.Match(["alpha.exe"])); // alphadat missing
    }

    [Fact]
    public void Most_specific_entry_wins()
    {
        var db = NewCatalog();
        db.Build([Entry("generic", "Generic", ["game.exe"], 5000), Entry("special", "Special", ["game.exe", "data.dat"], 12000)]);

        var match = db.Match(["game.exe", "data.dat"]);

        Assert.Equal(12000, match!.Profile.Cpu.FixedCycles);
    }

    [Fact]
    public void Telltales_match_content_paths_by_leaf_name()
    {
        var db = NewCatalog();
        db.Build([Entry("alpha", "Alpha Strike", ["alpha.exe"], 20000)]);

        Assert.NotNull(db.Match([@"GAMES\ALPHA\ALPHA.EXE"]));
        Assert.NotNull(db.Match(["games/alpha/Alpha.exe"]));
    }

    [Fact]
    public void Unknown_content_returns_null()
    {
        var db = NewCatalog();
        db.Build([Entry("alpha", "Alpha Strike", ["alpha.exe"], 1)]);

        Assert.Null(db.Match(["mystery.exe"]));
    }

    [Fact]
    public void Build_replaces_existing_catalog()
    {
        var db = NewCatalog();
        db.Build([Entry("a", "A Game", ["a.exe"], 1), Entry("b", "B Game", ["b.exe"], 1)]);
        Assert.Equal(2, db.Count);

        db.Build([Entry("c", "C Game", ["c.exe"], 1)]);

        Assert.Equal(1, db.Count);
        Assert.Null(db.Match(["a.exe"]));
        Assert.NotNull(db.Match(["c.exe"]));
    }

    [Fact]
    public void Matches_by_normalized_title_or_alias_when_telltales_are_absent()
    {
        var db = NewCatalog();
        db.Build([Entry("gamma", "GAMMA QUEST II", [], 20000) with { Aliases = ["Gamma Quest: The Sequel"] }]);

        Assert.NotNull(db.MatchByTitle("Gamma Quest 2"));
        Assert.NotNull(db.MatchByTitle("gamma quest ii (1993)"));
        Assert.NotNull(db.MatchByTitle("Gamma Quest - The Sequel"));
        Assert.Equal(CatalogMatchKind.Title, db.MatchByTitle("Gamma Quest 2")!.Kind);
        Assert.Null(db.MatchByTitle("Gamma"));
        Assert.Null(db.MatchByTitle("Delta Quest 2"));
    }

    [Fact]
    public void A_whole_title_hit_beats_a_subtitle_stripped_one()
    {
        var db = NewCatalog();
        db.Build([
            Entry("short", "Omega", [], 1000),
            Entry("long", "Omega - Director's Cut", [], 2000),
        ]);

        Assert.Equal(2000, db.MatchByTitle("Omega - Director's Cut")!.Profile.Cpu.FixedCycles);
        Assert.Equal(1000, db.MatchByTitle("Omega")!.Profile.Cpu.FixedCycles);
    }

    [Fact]
    public void Stores_no_readable_names()
    {
        var path = NewPath();
        var db = new CatalogDatabase(path);
        var entry = Entry("zeta", "Zeta Frontier Chronicles", ["ZETAFRNT.EXE", "ZETAWORLD.DAT"], 3000) with
        {
            Aliases = ["Zeta Frontier Deluxe"],
            Launch = ["ZETAGO.BAT", "ZETAFRNT.EXE"],
            Profile = new GameProfile
            {
                Title = "Zeta Frontier Chronicles",
                Launch = new LaunchSpec { Executable = "ZETAFRNT.EXE", PreCommands = ["CD ZETADIR"] },
                Mounts = [new MountSpec { Path = "zetacd.iso", Label = "ZETAVOLUME" }],
            },
        };
        db.Build([entry], revision: 7);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var text = Encoding.Latin1.GetString(File.ReadAllBytes(path)).ToLowerInvariant();
        foreach (var name in new[] { "zeta", "frontier", "zetafrnt", "zetaworld", "zetago", "zetadir", "zetacd", "zetavolume" })
            Assert.DoesNotContain(name, text);
        Assert.Empty(CatalogSource.FindLeaks(path, [entry]));

        // Still fully usable through the hashed keys.
        Assert.NotNull(db.Match(["zetafrnt.exe", "ZETAWORLD.DAT"]));
        Assert.NotNull(db.MatchByTitle("Zeta Frontier Deluxe"));
        Assert.Equal(7, db.Revision);
    }

    [Fact]
    public void The_stored_profile_keeps_settings_but_no_identity_or_launch_line()
    {
        var db = NewCatalog();
        db.Build([Entry("alpha", "Alpha Strike", ["alpha.exe"], 4000) with
        {
            Profile = new GameProfile
            {
                Title = "Alpha Strike",
                Cpu = new CpuSpec { CyclesMode = CyclesMode.Fixed, FixedCycles = 4000 },
                Machine = new MachineSpec { Machine = MachineType.Ega },
                Launch = new LaunchSpec { Executable = "ALPHA.EXE", Arguments = "-x", PreCommands = ["CD ALPHA"] },
            },
        }]);

        var profile = db.Match(["alpha.exe"])!.Profile;

        Assert.Equal(MachineType.Ega, profile.Machine.Machine);
        Assert.Equal(4000, profile.Cpu.FixedCycles);
        Assert.Equal(string.Empty, profile.Title);
        Assert.Null(profile.Launch.Executable);
        Assert.Null(profile.Launch.Arguments);
        Assert.Empty(profile.Launch.PreCommands);
        Assert.Equal(ProfileOrigin.CuratedBase, profile.Origin);
    }

    [Fact]
    public void Launch_candidates_resolve_in_preference_order_against_the_content()
    {
        var db = NewCatalog();
        db.Build([Entry("alpha", "Alpha Strike", ["alpha.exe"], 1) with { Launch = ["ALPHAGO.BAT", "ALPHA.EXE"] }]);
        var match = db.Match(["ALPHA.EXE"])!;

        Assert.Equal(@"ALPHA\ALPHA.EXE", match.FindLaunch([@"ALPHA\ALPHA.EXE", @"ALPHA\SETUP.EXE"]));
        Assert.Equal(@"ALPHAGO.BAT", match.FindLaunch([@"ALPHA\ALPHA.EXE", "ALPHAGO.BAT"]));
        Assert.Null(match.FindLaunch([@"ALPHA\INSTALL.EXE"]));
    }

    [Fact]
    public void The_shallowest_copy_of_a_launch_program_wins()
    {
        var db = NewCatalog();
        db.Build([Entry("alpha", "Alpha Strike", ["alpha.exe"], 1) with { Launch = ["ALPHA.EXE"] }]);
        var match = db.Match(["ALPHA.EXE"])!;

        Assert.Equal(@"GAME\ALPHA.EXE", match.FindLaunch([@"BACKUP\OLD\ALPHA.EXE", @"GAME\ALPHA.EXE"]));
    }

    [Fact]
    public void An_old_format_catalog_is_rebuilt_empty_on_open()
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE CatalogEntries (Id TEXT PRIMARY KEY, Title TEXT NOT NULL, NormalizedTitle TEXT NOT NULL,
                    NormalizedPrimary TEXT NOT NULL, ProfileJson TEXT NOT NULL);
                CREATE TABLE Telltales (EntryId TEXT NOT NULL, FileName TEXT NOT NULL);
                INSERT INTO CatalogEntries VALUES ('x', 'Plain Name', 'plainname', 'plainname', '{}');
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }

        var db = new CatalogDatabase(path);

        Assert.Equal(0, db.Count);
        Assert.Equal(CatalogDatabase.FormatVersion, CatalogDatabase.ReadInfo(path)!.Format);
    }

    [Fact]
    public void ReadInfo_reports_format_revision_and_size_and_rejects_non_catalogs()
    {
        var path = NewPath();
        var db = new CatalogDatabase(path);
        db.Build([Entry("a", "A Game", ["a.exe"], 1), Entry("b", "B Game", ["b.exe"], 1)], revision: 12);

        Assert.Equal(new CatalogInfo(CatalogDatabase.FormatVersion, 12, 2), CatalogDatabase.ReadInfo(path));
        Assert.Null(CatalogDatabase.ReadInfo(path + ".missing"));

        var junk = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(junk)!);
        File.WriteAllText(junk, "this is not a database, it is a long enough text file to have a header");
        Assert.Null(CatalogDatabase.ReadInfo(junk));
    }

    [Theory]
    [InlineData("GAME II", "Game 2")]
    [InlineData("Quest Saga II (1991)", "quest saga 2")]
    [InlineData("The Secret of the Island", "Secret of the Island")]
    public void Title_normalization_collapses_variants(string a, string b) =>
        Assert.Equal(CatalogDatabase.NormalizeTitle(a), CatalogDatabase.NormalizeTitle(b));

    [Fact]
    public void Hash_is_stable_and_domain_separated()
    {
        Assert.Equal(32, CatalogHash.Of("alpha").Length);
        Assert.Equal(CatalogHash.Of("alpha"), CatalogHash.Of("alpha"));
        Assert.NotEqual(CatalogHash.Of("alpha"), CatalogHash.Of("alphb"));
        Assert.Equal(string.Empty, CatalogHash.Of(string.Empty));
        // Not the bare SHA-256 of the input (which starts 8ed3f6ad for "alpha").
        Assert.False(CatalogHash.Of("alpha").StartsWith("8ed3f6ad", StringComparison.Ordinal));
    }

    private static string NewPath() =>
        Path.Combine(Path.GetTempPath(), "emudos-tests", Guid.NewGuid().ToString("N"), "catalog.db");

    private static CatalogDatabase NewCatalog() => new(NewPath());

    private static CatalogEntry Entry(string id, string title, string[] telltales, int cycles) => new()
    {
        Id = id,
        Title = title,
        Telltales = telltales,
        Profile = new GameProfile
        {
            Title = title,
            Origin = ProfileOrigin.CuratedBase,
            Cpu = new CpuSpec { CyclesMode = CyclesMode.Fixed, FixedCycles = cycles },
        },
    };
}

public class CatalogSourceTests
{
    [Fact]
    public void FindLeaks_reports_a_readable_name()
    {
        // Positive control for the leak scan: a file that DOES carry the names must be reported.
        var path = Path.Combine(Path.GetTempPath(), "emudos-tests", Guid.NewGuid().ToString("N"), "leaky.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. Encoding.UTF8.GetBytes("xx Zeta Frontier xx "), .. Encoding.Unicode.GetBytes("ZETAWORLD.DAT")]);
        var entry = new CatalogEntry
        {
            Id = "z",
            Title = "Zeta Frontier",
            Telltales = ["ZETAWORLD.DAT"],
            Launch = ["ZETAGO.BAT"],
            Profile = new GameProfile(),
        };

        var leaks = CatalogSource.FindLeaks(path, [entry]);

        Assert.Contains("Zeta Frontier", leaks);
        Assert.Contains("ZETAWORLD.DAT", leaks);
        Assert.DoesNotContain("ZETAGO.BAT", leaks);
    }

    [Fact]
    public void Build_writes_a_hashed_catalog_from_a_source_list()
    {
        var dir = Path.Combine(Path.GetTempPath(), "emudos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "source.json");
        File.WriteAllText(source, """
            // comment lines are allowed
            { "revision": 4, "entries": [
              { "title": "Zeta Frontier", "aliases": ["Zeta Deluxe"], "telltales": ["ZETAFRNT.EXE"], "launch": ["ZETAGO.BAT", "ZETAFRNT.EXE"],
                "profile": { "cpu": { "cyclesMode": "Fixed", "fixedCycles": 3000 }, "machine": { "machine": "Ega" } } },
              { "title": "Omega Run", "telltales": [], "launch": ["OMEGA.EXE"] },
            ] }
            """);
        var output = Path.Combine(dir, "catalog.db");

        var report = CatalogSource.Build(source, output);

        Assert.Equal(new CatalogBuildReport(2, 4, []), report with { Leaks = [] });
        Assert.Empty(report.Leaks);
        var db = new CatalogDatabase(output);
        var match = db.Match(["ZETAFRNT.EXE"])!;
        Assert.Equal(3000, match.Profile.Cpu.FixedCycles);
        Assert.Equal(MachineType.Ega, match.Profile.Machine.Machine);
        Assert.Equal("ZETAFRNT.EXE", match.FindLaunch(["ZETAFRNT.EXE"]));
        Assert.NotNull(db.MatchByTitle("Omega Run"));
    }

    [Fact]
    public void Build_rejects_duplicate_titles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "emudos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "source.json");
        File.WriteAllText(source, """{ "revision": 1, "entries": [ { "title": "Zeta" }, { "title": "zeta" } ] }""");

        Assert.Throws<InvalidDataException>(() => CatalogSource.Build(source, Path.Combine(dir, "c.db")));
    }
}
