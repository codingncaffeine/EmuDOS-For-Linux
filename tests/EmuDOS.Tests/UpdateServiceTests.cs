using EmuDOS.Services;
using static EmuDOS.Services.UpdateService;

namespace EmuDOS.Tests;

public class UpdateServiceTests : IDisposable
{
    private const string SystemInstall = "/usr/lib/emudos";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"emudos-update-tests-{Guid.NewGuid():N}");

    public UpdateServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // A dpkg database (/var/lib/dpkg/info) holding the given <package>.list files.
    private string DpkgInfo(params (string Name, string[] Files)[] lists)
    {
        var dir = Path.Combine(_root, "dpkg-info");
        Directory.CreateDirectory(dir);
        foreach (var (name, files) in lists)
            File.WriteAllLines(Path.Combine(dir, name), files);
        return dir;
    }

    [Theory]
    [InlineData("emudos.list")]
    [InlineData("emudos:amd64.list")]
    public void System_install_registered_by_dpkg_is_a_deb(string listName)
    {
        var info = DpkgInfo((listName, ["/.", "/usr/lib/emudos", "/usr/lib/emudos/EmuDOS", "/usr/bin/emudos"]));
        Assert.Equal(InstallKind.Deb, DetectInstallKind(SystemInstall, info));
        Assert.Equal(InstallKind.Deb, DetectInstallKind(SystemInstall + "/", info));
    }

    [Fact]
    public void System_install_without_a_dpkg_database_is_package_managed()
    {
        // Arch without dpkg: the AUR's emudos-bin owns /usr/lib/emudos.
        Assert.Equal(InstallKind.PackageManaged, DetectInstallKind(SystemInstall, Path.Combine(_root, "absent")));
    }

    [Fact]
    public void System_install_dpkg_does_not_list_is_package_managed()
    {
        // Arch WITH dpkg installed: dpkg's database exists but never registered EmuDOS.
        var info = DpkgInfo(("libc6:amd64.list", ["/usr/lib/x86_64-linux-gnu/libc.so.6"]));
        Assert.Equal(InstallKind.PackageManaged, DetectInstallKind(SystemInstall, info));
    }

    [Fact]
    public void Only_the_emudos_package_list_counts()
    {
        // Another package's list naming the path, and an emudos list naming a different binary.
        var info = DpkgInfo(
            ("emudos-data.list", ["/usr/lib/emudos/EmuDOS"]),
            ("emudos.list", ["/opt/emudos/EmuDOS", "/usr/lib/emudos/EmuDOS.dll"]));
        Assert.Equal(InstallKind.PackageManaged, DetectInstallKind(SystemInstall, info));
    }

    [Fact]
    public void Build_tree_is_dev_even_under_usr()
    {
        var info = DpkgInfo(("emudos.list", ["/usr/src/EmuDOS/bin/Release/net10.0/EmuDOS"]));
        Assert.Equal(InstallKind.Dev, DetectInstallKind("/usr/src/EmuDOS/bin/Release/net10.0", info));
        Assert.Equal(InstallKind.Dev, DetectInstallKind("/home/u/EmuDOS/src/EmuDOS/bin/Debug", info));
    }

    [Fact]
    public void Writable_folder_is_self_contained_and_unwritable_one_is_read_only()
    {
        var info = DpkgInfo();
        var extracted = Path.Combine(_root, "EmuDOS-0.5.0");
        Directory.CreateDirectory(extracted);
        Assert.Equal(InstallKind.SelfContained, DetectInstallKind(extracted, info));
        Assert.False(File.Exists(Path.Combine(extracted, ".write-probe")));

        // /usr/local is the administrator's, not a package manager's: it falls through to the probe.
        Assert.Equal(InstallKind.ReadOnly, DetectInstallKind(Path.Combine(_root, "missing"), info));
        Assert.Equal(InstallKind.ReadOnly, DetectInstallKind("/usr/local/lib/emudos-test-" + Guid.NewGuid().ToString("N"), info));
    }

    [Fact]
    public async Task Package_managed_install_gets_no_asset_and_is_never_applied()
    {
        ReleaseAsset[] assets =
        [
            new("EmuDOS-0.6.0-linux-x64.tar.gz", "https://example.invalid/t", 1),
            new("emudos_0.6.0_amd64.deb", "https://example.invalid/d", 1),
        ];
        Assert.Null(PickAsset(InstallKind.PackageManaged, assets));
        Assert.Equal("emudos_0.6.0_amd64.deb", PickAsset(InstallKind.Deb, assets)?.Name);

        // Refused before any download; the asset URL would not resolve anyway.
        var update = new AppUpdate("v0.6.0", true, assets[1], InstallKind.PackageManaged);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(update));
        Assert.Contains("package manager", ex.Message);
    }
}
