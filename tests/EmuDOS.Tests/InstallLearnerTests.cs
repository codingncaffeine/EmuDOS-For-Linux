using System.IO.Compression;
using EmuDOS.Core.Catalog;
using EmuDOS.Core.Import;
using EmuDOS.Core.Library;
using EmuDOS.Core.Model;

namespace EmuDOS.Tests;

public class InstallLearnerTests
{
    [Fact]
    public void A_folder_install_teaches_the_new_game_program()
    {
        var (content, saves) = Box(("INSTALL.BAT", 150), ("ZQ.1", 90000));
        ContentBaseline.Capture(content, saves);
        Write(content, "ZQ/ZQ.EXE", 60000);
        Write(content, "ZQ/SETUP.EXE", 20000);

        var learned = InstallLearner.Learn(content, saves, Profile("Zeta Quest"), new GameUserState(), resolver: null);

        Assert.Equal(new InstallLearning(@"ZQ\ZQ.EXE", false, null, false), learned);
    }

    [Fact]
    public void Nothing_new_teaches_nothing()
    {
        var (content, saves) = Box(("ZQ.EXE", 60000));
        ContentBaseline.Capture(content, saves);
        Write(content, "SAVEGAME.001", 400); // an in-game save is not a program

        Assert.Null(InstallLearner.Learn(content, saves, Profile("Zeta Quest"), new GameUserState(), null));
    }

    [Fact]
    public void A_session_that_only_produced_a_setup_program_teaches_nothing()
    {
        var (content, saves) = Box(("INSTALL.BAT", 150));
        ContentBaseline.Capture(content, saves);
        Write(content, "ZQ/SNDSETUP.EXE", 20000);

        Assert.Null(InstallLearner.Learn(content, saves, Profile("Zeta Quest"), new GameUserState(), null));
    }

    [Fact]
    public void A_program_learned_before_is_kept()
    {
        var (content, saves) = Box(("INSTALL.BAT", 150));
        ContentBaseline.Capture(content, saves);
        Write(content, "ZQ/ZQ.EXE", 60000);

        var state = new GameUserState { InstalledExecutable = @"ZQ\ZQ.EXE" };

        Assert.Null(InstallLearner.Learn(content, saves, Profile("Zeta Quest"), state, null));
    }

    [Fact]
    public void The_catalog_names_the_program_and_its_settings_are_adopted()
    {
        var (content, saves) = Box(("INSTALL.BAT", 150));
        ContentBaseline.Capture(content, saves);
        Write(content, "ZQ/ZQGAME.EXE", 60000);
        Write(content, "ZQ/ZQLOAD.BAT", 60);
        var resolver = new ProfileResolver(Catalog(launch: ["ZQLOAD.BAT"], telltales: ["ZQGAME.EXE"], cycles: 4000));

        var learned = InstallLearner.Learn(content, saves, Profile("Zeta Quest"), new GameUserState(), resolver);

        Assert.NotNull(learned);
        Assert.Equal(@"ZQ\ZQLOAD.BAT", learned!.Program);
        Assert.True(learned.Recognized);
        Assert.Equal(4000, learned.Profile!.Cpu.FixedCycles);
        Assert.Equal(@"ZQ\ZQLOAD.BAT", learned.Profile.Launch.Executable);
    }

    [Fact]
    public void A_user_override_keeps_its_settings()
    {
        var (content, saves) = Box(("INSTALL.BAT", 150));
        ContentBaseline.Capture(content, saves);
        Write(content, "ZQ/ZQGAME.EXE", 60000);
        var resolver = new ProfileResolver(Catalog(launch: ["ZQGAME.EXE"], telltales: ["ZQGAME.EXE"], cycles: 4000));
        var mine = Profile("Zeta Quest") with { Origin = ProfileOrigin.UserOverride };

        var learned = InstallLearner.Learn(content, saves, mine, new GameUserState(), resolver);

        // The override blocks the catalog, so the program comes from the guess and no profile is adopted.
        Assert.Equal(new InstallLearning(@"ZQ\ZQGAME.EXE", false, null, false), learned);
    }

    [Fact]
    public void A_cd_install_on_the_persisted_drive_is_pinned_to_auto_start()
    {
        var (_, saves) = Box();
        var zip = Path.Combine(saves, "game.pure.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Entry(archive, "ZQ/ZQ.EXE", 60000);
            Entry(archive, "ZQ/INSTALL.EXE", 90000);
            Entry(archive, "ZQ/ZQ.DAT", 500000);
        }
        var profile = Profile("Zeta Quest") with { SourceMedia = SourceMediaType.Iso };

        var learned = InstallLearner.Learn(Path.Combine(saves, "..", "content"), saves, profile, new GameUserState(), null);

        Assert.Equal(new InstallLearning(@"C:\ZQ\ZQ.EXE", false, null, true), learned);
        using var check = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(check.GetEntry("AUTOBOOT.DBP")!.Open());
        Assert.Equal("C:\\ZQ\\ZQ.EXE\r\n", reader.ReadToEnd());
    }

    [Fact]
    public void A_pinned_cd_game_is_left_alone()
    {
        var (_, saves) = Box();
        using (var archive = ZipFile.Open(Path.Combine(saves, "game.pure.zip"), ZipArchiveMode.Create))
        {
            Entry(archive, "ZQ/ZQ.EXE", 60000);
            Entry(archive, "AUTOBOOT.DBP", 20);
        }

        Assert.Null(InstallLearner.Learn(saves, saves, Profile("Zeta Quest") with { SourceMedia = SourceMediaType.Iso },
            new GameUserState(), null));
    }

    private static GameProfile Profile(string title) => new() { Title = title, Launch = new LaunchSpec { Executable = "INSTALL.BAT" } };

    private static CatalogDatabase Catalog(string[] launch, string[] telltales, int cycles)
    {
        var db = new CatalogDatabase(Path.Combine(Path.GetTempPath(), "emudos-tests", Guid.NewGuid().ToString("N"), "catalog.db"));
        db.Build([new CatalogEntry
        {
            Id = "zq",
            Title = "Zeta Quest Deluxe",
            Telltales = telltales,
            Launch = launch,
            Profile = new GameProfile { Cpu = new CpuSpec { CyclesMode = CyclesMode.Fixed, FixedCycles = cycles } },
        }]);
        return db;
    }

    private static (string Content, string Saves) Box(params (string Path, int Size)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "emudos-tests", Guid.NewGuid().ToString("N"));
        var content = Path.Combine(root, "content");
        var saves = Path.Combine(root, "saves");
        Directory.CreateDirectory(content);
        Directory.CreateDirectory(saves);
        foreach (var (path, size) in files)
            Write(content, path, size);
        return (content, saves);
    }

    private static void Write(string root, string relPath, int size)
    {
        var full = Path.Combine(root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
    }

    private static void Entry(ZipArchive archive, string name, int size)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(new byte[size]);
    }
}
