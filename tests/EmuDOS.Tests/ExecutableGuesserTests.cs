using EmuDOS.Core.Import;

namespace EmuDOS.Tests;

public class ExecutableGuesserTests
{
    [Fact]
    public void An_exact_title_word_beats_a_name_that_only_contains_it()
    {
        var dir = Content(("GLBLASTR.EXE", 900), ("BLASTR.EXE", 100));

        Assert.Equal((ImportClassification.ReadyToPlay, "BLASTR.EXE"), Guess(dir, "Blastr"));
    }

    [Fact]
    public void A_batch_beats_the_program_of_the_same_name_beside_it()
    {
        var dir = Content(("RACER.EXE", 900), ("RACER.BAT", 20));

        Assert.Equal("RACER.BAT", Guess(dir, "Racer").Executable);
    }

    [Fact]
    public void Helper_and_backup_copies_sort_behind_the_plain_name()
    {
        var dir = Content(("_RACER.BAT", 90), ("RACER.BAT", 20));

        Assert.Equal("RACER.BAT", Guess(dir, "Racer").Executable);
    }

    [Fact]
    public void Installer_tools_are_never_the_game()
    {
        var dir = Content(("DEICE.EXE", 17000), ("INSTALL.BAT", 150));

        Assert.Equal((ImportClassification.NeedsInstall, "INSTALL.BAT"), Guess(dir, "Some Game"));
    }

    [Fact]
    public void Only_utilities_beside_an_installer_means_it_needs_installing()
    {
        var dir = Content(("INFO1.COM", 11000), ("INSTALL.BAT", 75), ("README.BAT", 20));

        Assert.Equal((ImportClassification.NeedsInstall, "INSTALL.BAT"), Guess(dir, "Some Game"));
    }

    [Fact]
    public void An_installer_in_a_subfolder_is_still_an_installer()
    {
        // DOS paths use '\', which is not a separator on Linux: reading the stem as "c\instgame"
        // misses the INST* rule and makes the (larger) installer the game.
        var dir = Content(("C/INSTGAME.EXE", 90000), ("C/ZQ.EXE", 6000));

        Assert.Equal(@"C\ZQ.EXE", Guess(dir, "Zeta Quest").Executable);
    }

    [Fact]
    public void A_batch_that_starts_dosbox_itself_is_skipped()
    {
        var dir = Content(("C/ZQ2.COM", 2900));
        File.WriteAllText(Path.Combine(dir, "!start.bat"), "@echo off\r\ndosbox -conf dosbox.conf\r\n");

        Assert.Equal(@"C\ZQ2.COM", Guess(dir, "Zeta Quest 2").Executable);
    }

    [Fact]
    public void Graphics_mode_builds_lose_to_their_loader()
    {
        var dir = Content(("RX.EXE", 17000), ("RXEGA.EXE", 66000), ("RXCGA.EXE", 57000));

        Assert.Equal("RX.EXE", Guess(dir, "Something Else").Executable);
    }

    [Fact]
    public void The_largest_real_program_wins_without_a_title_match()
    {
        var dir = Content(("ALPHA.EXE", 3000), ("OMEGA.EXE", 90000), ("HELPME.EXE", 500000), ("COMMAND.COM", 800000));

        Assert.Equal("OMEGA.EXE", Guess(dir, "Unrelated Title").Executable);
    }

    [Fact]
    public void A_windows_build_loses_to_the_dos_program()
    {
        var dir = Content(("ZETA95.EXE", 2_700_000), ("ZETADOS.EXE", 900_000));

        Assert.Equal("ZETADOS.EXE", Guess(dir, "Unrelated Title").Executable);
    }

    [Fact]
    public void An_extender_game_starts_from_its_batch_when_the_batch_is_named_like_it()
    {
        var dir = Content(("ZETADOS.BAT", 50), ("ZETADOS/DOS4GW.EXE", 265000), ("ZETADOS/ZETA24.EXE", 2_000_000),
                          ("EXTRAS.BAT", 4000));

        Assert.Equal("ZETADOS.BAT", Guess(dir, "Unrelated Title").Executable);
    }

    [Fact]
    public void An_unrelated_batch_does_not_steal_an_extender_game()
    {
        var dir = Content(("NETPACK.BAT", 4000), ("DOS4GW.EXE", 265000), ("ZETASHELL.EXE", 960_000), ("ZETA.EXE", 940_000));

        Assert.Equal("ZETASHELL.EXE", Guess(dir, "Unrelated Title").Executable);
    }

    [Fact]
    public void Sizes_can_come_from_an_archive_listing()
    {
        var sizes = new Dictionary<string, long> { [@"G\SMALL.EXE"] = 10, [@"G\BIG.EXE"] = 10_000 };

        var guess = ExecutableGuesser.Guess([@"G\BIG.EXE", @"G\SMALL.EXE"], "Unrelated", sizes: sizes);

        Assert.Equal(@"G\BIG.EXE", guess.Executable);
    }

    private static (ImportClassification Classification, string? Executable) Guess(string dir, string title)
    {
        var exes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".exe" or ".com" or ".bat")
            .Select(f => Path.GetRelativePath(dir, f).Replace('/', '\\'))
            .ToList();
        return ExecutableGuesser.Guess(exes, title, dir);
    }

    private static string Content(params (string Path, int Size)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "emudos-tests", Guid.NewGuid().ToString("N"));
        foreach (var (path, size) in files)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[size]);
        }
        Directory.CreateDirectory(dir);
        return dir;
    }
}
