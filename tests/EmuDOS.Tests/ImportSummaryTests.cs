using EmuDOS.Core.Import;

namespace EmuDOS.Tests;

public class TitleCleanerTests
{
    [Theory]
    [InlineData("Zeta_Quest_1_(1990)", "Zeta Quest 1")]
    [InlineData("Zeta Quest (1995)(Some Publisher)", "Zeta Quest")]
    [InlineData("Zeta Quest (Disc 1) [!]", "Zeta Quest")]
    [InlineData("Zeta Quest - The Sequel (USA)", "Zeta Quest - The Sequel")]
    [InlineData("Eleventh Hour, The (1995)", "The Eleventh Hour")]
    [InlineData("Zeta-Quest-The-Challenge_DOS_EN", "Zeta Quest The Challenge")]
    [InlineData("Zeta_Quest_1994", "Zeta Quest")]
    [InlineData("Zeta Quest 2000", "Zeta Quest 2000")]
    [InlineData("Zeta Quest 1994", "Zeta Quest 1994")]
    [InlineData("Z-Fighter", "Z-Fighter")]
    [InlineData("Hi-Zeta (1995)", "Hi-Zeta")]
    [InlineData("Zeta Quest!", "Zeta Quest!")]
    [InlineData("(Untitled)", "(Untitled)")]
    [InlineData("   ", "Untitled")]
    public void Cleans_dump_style_names(string name, string expected) =>
        Assert.Equal(expected, TitleCleaner.Clean(name));

    [Fact]
    public async Task Import_uses_the_cleaned_title()
    {
        var root = Path.Combine(Path.GetTempPath(), "emudos-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Zeta_Quest_1_(1990)");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "ZQ.EXE"), "x");
        var pipeline = new ImportPipeline(new Core.Infrastructure.AppPaths(Path.Combine(root, "data")), new Core.Library.GameboxStore());

        var result = await pipeline.ImportAsync(source);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Zeta Quest 1", result.Title);
        Assert.Equal("Zeta Quest 1", Path.GetFileName(result.GameboxPath));
    }
}

public class ImportSummaryTests
{
    [Fact]
    public void Names_the_catalog_program()
    {
        var line = ImportSummary.Describe(Result(ImportClassification.ReadyToPlay, LaunchSource.Catalog, @"ZQ\ZQ.EXE", recognised: true));

        Assert.Equal("Imported Zeta Quest — recognised by the catalog; it runs ZQ.EXE.", line);
    }

    [Fact]
    public void Says_a_guess_is_a_guess()
    {
        var line = ImportSummary.Describe(Result(ImportClassification.ReadyToPlay, LaunchSource.Guess, "ZQ.EXE", recognised: false));

        Assert.Equal("Imported Zeta Quest — not in the catalog; guessed ZQ.EXE (use Choose program… if that's wrong).", line);
    }

    [Fact]
    public void Says_what_to_run_when_it_needs_installing()
    {
        var line = ImportSummary.Describe(Result(ImportClassification.NeedsInstall, LaunchSource.Guess, "INSTALL.BAT", recognised: true));

        Assert.Equal("Imported Zeta Quest — recognised by the catalog; it needs installing: open it to run INSTALL.BAT.", line);
    }

    [Fact]
    public void A_disc_to_install_from_says_where_the_disc_is()
    {
        var line = ImportSummary.Describe(Result(ImportClassification.NeedsInstall, LaunchSource.None, null, recognised: false));

        Assert.Equal("Imported Zeta Quest — open it to install (the disc is mounted as D:).", line);
    }

    [Fact]
    public void A_warning_wins()
    {
        var line = ImportSummary.Describe(Result(ImportClassification.NeedsInstall, LaunchSource.None, null, recognised: false)
            with { Warning = "That disc can't be read." });

        Assert.Equal("That disc can't be read.", line);
    }

    [Fact]
    public void A_batch_counts_recognised_guessed_and_to_install()
    {
        var line = ImportSummary.Describe([
            Result(ImportClassification.ReadyToPlay, LaunchSource.Catalog, "A.EXE", recognised: true),
            Result(ImportClassification.ReadyToPlay, LaunchSource.Catalog, "B.EXE", recognised: true),
            Result(ImportClassification.ReadyToPlay, LaunchSource.Guess, @"C\ZQ.EXE", recognised: false),
            Result(ImportClassification.NeedsInstall, LaunchSource.Guess, "INSTALL.EXE", recognised: false),
        ]);

        Assert.Equal("Imported 4 games — 2 recognised by the catalog, 1 guessed (Zeta Quest runs ZQ.EXE), 1 to install.", line);
    }

    private static ImportResult Result(ImportClassification cls, LaunchSource source, string? exe, bool recognised) => new()
    {
        Success = true,
        Title = "Zeta Quest",
        Classification = cls,
        LaunchSource = source,
        ChosenExecutable = exe,
        CatalogRecognized = recognised,
    };
}
