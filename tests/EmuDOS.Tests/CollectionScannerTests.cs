using EmuDOS.Core.Import;

namespace EmuDOS.Tests;

public class CollectionScannerTests
{
    [Fact]
    public void A_folder_of_three_games_yields_three_games()
    {
        var root = Tree("Alpha Run/ALPHA.EXE", "Beta Blast/BB/BETA.EXE", "Gamma Quest/GQ.COM", "cover.jpg");

        var items = CollectionScanner.Expand([root]);

        Assert.Equal(["Alpha Run", "Beta Blast", "Gamma Quest"], items.Select(Path.GetFileName).Order());
    }

    [Fact]
    public void A_single_game_folder_still_yields_one()
    {
        var root = Tree("ALPHA.EXE", "DATA/LEVELS.DAT", "SOUND/SETSOUND.EXE", "EXTRAS/EDITOR.EXE");

        Assert.Equal([root], CollectionScanner.Expand([root]));
    }

    [Fact]
    public void Subfolders_named_like_parts_of_one_game_are_not_games()
    {
        // A hybrid CD dump: nothing runs at the top, DOS\ and WIN\ each hold an installer.
        var root = Tree("DOS/INSTALL.EXE", "WIN/SETUP.EXE", "README.TXT");

        Assert.Null(CollectionScanner.Items(root));
    }

    [Fact]
    public void A_repack_whose_top_only_starts_dosbox_is_one_game()
    {
        var root = Tree("dosbox.exe", "C/ALPHA.EXE", "C/BETA.EXE");
        File.WriteAllText(Path.Combine(root, "!start.bat"), "@echo off\r\ndosbox -conf dosbox.conf\r\n");

        Assert.Null(CollectionScanner.Items(root));
    }

    [Fact]
    public void A_program_folder_beside_its_disc_folder_is_one_game()
    {
        var root = Tree("Alpha/ALPHA.EXE", "Alpha CD/ALPHA.ISO");

        Assert.Null(CollectionScanner.Items(root));
    }

    [Fact]
    public void Archives_and_disc_sets_count_as_games_and_cue_tracks_stay_with_their_cue()
    {
        var root = Tree("Alpha Run.zip", "Beta (Disc 1).iso", "Beta (Disc 2).iso", "Gamma.bin");
        File.WriteAllText(Path.Combine(root, "Gamma.cue"), "FILE \"Gamma.bin\" BINARY\r\n  TRACK 01 MODE1/2352\r\n");

        var items = CollectionScanner.Items(root)!;

        Assert.Equal(["Alpha Run.zip", "Beta (Disc 1).iso", "Beta (Disc 2).iso", "Gamma.cue"],
            items.Select(Path.GetFileName).Order());
    }

    [Fact]
    public void One_multi_disc_set_is_one_game()
    {
        var root = Tree("Beta-CD1of4.iso", "Beta-CD2of4.iso", "Beta-CD3of4.iso", "Beta-CD4of4.iso");

        Assert.Null(CollectionScanner.Items(root));
        Assert.Equal("Beta", ImportPipeline.StripDiscMarker("Beta-CD3of4"));
    }

    [Fact]
    public void Nested_collections_expand_and_non_game_folders_are_left_out()
    {
        var root = Tree("Pack/Alpha/ALPHA.EXE", "Pack/Beta/BETA.EXE", "Solo/SOLO.EXE", "Manuals/solo.pdf", "roms/MT32_PCM.ROM");

        var items = CollectionScanner.Expand([root]);

        Assert.Equal(["Alpha", "Beta", "Solo"], items.Select(Path.GetFileName).Order());
    }

    [Fact]
    public void A_dropped_file_passes_through()
    {
        var root = Tree("Alpha Run.zip");
        var zip = Path.Combine(root, "Alpha Run.zip");

        Assert.Equal([zip], CollectionScanner.Expand([zip]));
    }

    private static string Tree(params string[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "emudos-tests", Guid.NewGuid().ToString("N"), "Drop");
        Directory.CreateDirectory(root);
        foreach (var file in files)
        {
            var full = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[16]);
        }
        return root;
    }
}
