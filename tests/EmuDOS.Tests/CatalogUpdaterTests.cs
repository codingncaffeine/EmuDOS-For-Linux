using System.Net;
using EmuDOS.Core.Catalog;
using EmuDOS.Core.Downloads;
using EmuDOS.Core.Infrastructure;
using EmuDOS.Core.Model;

namespace EmuDOS.Tests;

public class CatalogUpdaterTests
{
    [Fact]
    public void Installs_a_newer_catalog_over_an_older_one()
    {
        var installed = Built(revision: 1, "Alpha Strike");
        var candidate = Built(revision: 2, "Alpha Strike", "Beta Run");

        Assert.True(CatalogUpdater.InstallIfNewer(candidate, installed));

        Assert.Equal(new CatalogInfo(CatalogDatabase.FormatVersion, 2, 2), CatalogDatabase.ReadInfo(installed));
    }

    [Fact]
    public void Never_downgrades_or_reinstalls_the_same_revision()
    {
        var installed = Built(revision: 5, "Alpha Strike", "Beta Run");

        Assert.False(CatalogUpdater.InstallIfNewer(Built(revision: 4, "Gamma Quest"), installed));
        Assert.False(CatalogUpdater.InstallIfNewer(Built(revision: 5, "Gamma Quest"), installed));
        Assert.Equal(2, CatalogDatabase.ReadInfo(installed)!.Entries);
    }

    [Fact]
    public void Never_installs_an_empty_or_foreign_candidate()
    {
        var installed = Built(revision: 1, "Alpha Strike");
        var empty = Built(revision: 9);
        var junk = Path.Combine(TempRoot(), "junk.db");
        File.WriteAllText(junk, "<html>404 page served with a 200</html> padding padding padding padding");

        Assert.False(CatalogUpdater.InstallIfNewer(empty, installed));
        Assert.False(CatalogUpdater.InstallIfNewer(junk, installed));
        Assert.Equal(1, CatalogDatabase.ReadInfo(installed)!.Revision);
    }

    [Fact]
    public void Replaces_an_empty_or_missing_catalog_whatever_its_revision()
    {
        var missing = Path.Combine(TempRoot(), "sub", "catalog.db");
        Assert.True(CatalogUpdater.InstallIfNewer(Built(revision: 0, "Alpha Strike"), missing));
        Assert.Equal(1, CatalogDatabase.ReadInfo(missing)!.Entries);

        var emptyInstalled = Built(revision: 3);
        Assert.True(CatalogUpdater.InstallIfNewer(Built(revision: 1, "Alpha Strike"), emptyInstalled));
    }

    [Fact]
    public void An_open_catalog_sees_the_installed_update()
    {
        var path = Path.Combine(TempRoot(), "catalog.db");
        var live = new CatalogDatabase(path); // what the app holds open (pooled connections)
        live.Build([Entry("Alpha Strike")], revision: 1);
        Assert.NotNull(live.MatchByTitle("Alpha Strike"));

        Assert.True(CatalogUpdater.InstallIfNewer(Built(revision: 2, "Beta Run"), path));

        Assert.Equal(2, live.Revision);
        Assert.Null(live.MatchByTitle("Alpha Strike"));
        Assert.NotNull(live.MatchByTitle("Beta Run"));
    }

    [Fact]
    public async Task UpdateAsync_installs_a_newer_download_and_reports_it()
    {
        var root = TempRoot();
        var catalog = new CatalogDatabase(Path.Combine(root, "Catalog", "catalog.db"));
        catalog.Build([Entry("Alpha Strike")], revision: 1);
        var served = await File.ReadAllBytesAsync(Built(revision: 3, "Alpha Strike", "Beta Run"));
        using var http = new HttpClient(new FakeHttpMessageHandler(served));
        var updater = new CatalogUpdater(catalog, new DownloadService(http, new AppPaths(root)));

        var result = await updater.UpdateAsync();

        Assert.Equal(new CatalogUpdateResult(true, 3, 2, null), result);
        Assert.False(File.Exists(catalog.Path + ".download.tmp"));
    }

    [Fact]
    public async Task UpdateAsync_keeps_the_catalog_when_the_download_fails()
    {
        var root = TempRoot();
        var catalog = new CatalogDatabase(Path.Combine(root, "Catalog", "catalog.db"));
        catalog.Build([Entry("Alpha Strike")], revision: 4);
        using var http = new HttpClient(new FakeHttpMessageHandler([], HttpStatusCode.NotFound));
        var updater = new CatalogUpdater(catalog, new DownloadService(http, new AppPaths(root)));

        var result = await updater.UpdateAsync();

        Assert.False(result.Installed);
        Assert.NotNull(result.Error);
        Assert.Equal(4, result.Revision);
        Assert.Equal(1, result.Entries);
    }

    [Fact]
    public void The_embedded_baseline_is_a_real_catalog()
    {
        // Guards the release: a placeholder or missing baseline builds green and ships a catalog that
        // recognises nothing.
        using var stream = typeof(CatalogUpdater).Assembly.GetManifestResourceStream(CatalogUpdater.BaselineResource);
        Assert.NotNull(stream);
        var path = Path.Combine(TempRoot(), "baseline.db");
        using (var file = File.Create(path))
            stream!.CopyTo(file);

        var info = CatalogDatabase.ReadInfo(path);

        Assert.NotNull(info);
        Assert.Equal(CatalogDatabase.FormatVersion, info!.Format);
        Assert.True(info.Revision >= 1, $"baseline revision {info.Revision}");
        Assert.True(info.Entries >= 50, $"baseline has only {info.Entries} games");
    }

    [Fact]
    public void EnsureBaseline_installs_the_embedded_catalog_once()
    {
        var root = TempRoot();
        var catalog = new CatalogDatabase(Path.Combine(root, "catalog.db"));
        var updater = new CatalogUpdater(catalog, new DownloadService(new HttpClient(), new AppPaths(root)));

        Assert.True(updater.EnsureBaseline());
        Assert.True(catalog.Count >= 50);
        Assert.False(updater.EnsureBaseline()); // same revision: nothing to do
        Assert.False(File.Exists(catalog.Path + ".baseline.tmp"));
    }

    private static string Built(int revision, params string[] titles)
    {
        var path = Path.Combine(TempRoot(), "catalog.db");
        new CatalogDatabase(path).Build(titles.Select(Entry), revision);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return path;
    }

    private static CatalogEntry Entry(string title) => new()
    {
        Id = title,
        Title = title,
        Telltales = [title.Replace(" ", "") + ".exe"],
        Profile = new GameProfile(),
    };

    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "emudos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
