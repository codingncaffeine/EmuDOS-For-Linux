using EmuDOS.Core.Catalog;
using EmuDOS.Core.Downloads;
using EmuDOS.Core.Import;
using EmuDOS.Core.Infrastructure;
using EmuDOS.Core.Model;
using SharpCompress.Archives;

namespace EmuDOS.Tests.Integration;

/// <summary>
/// Opt-in diagnostic: for every folder and archive in a game library, what import would launch
/// with the heuristics alone and with the built-in catalog. Read-only — archives are listed, never
/// extracted. Runs when <c>EMUDOS_AUDIT_GAMES</c> (the library) and <c>EMUDOS_AUDIT_REPORT</c> (a
/// TSV to write) are set; otherwise a no-op.
/// </summary>
public class CatalogAuditLiveTests
{
    private static readonly string[] ExecutableExtensions = [".exe", ".com", ".bat"];
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar"];

    [Fact]
    public void Reports_heuristic_and_catalog_picks_for_a_library()
    {
        var root = Environment.GetEnvironmentVariable("EMUDOS_AUDIT_GAMES");
        var report = Environment.GetEnvironmentVariable("EMUDOS_AUDIT_REPORT");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(report) || !Directory.Exists(root))
            return;

        var scratch = Path.Combine(Path.GetTempPath(), "emudos-tests", Guid.NewGuid().ToString("N"));
        var catalog = new CatalogDatabase(Path.Combine(scratch, "catalog.db"));
        new CatalogUpdater(catalog, new DownloadService(new HttpClient(), new AppPaths(scratch))).EnsureBaseline();
        var resolver = new ProfileResolver(catalog);

        var rows = new List<string> { "item\ttitle\tclass\theuristic\trecognized\tcatalog\tfinal" };
        foreach (var item in Directory.EnumerateFileSystemEntries(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var isDir = Directory.Exists(item);
            if (!isDir && !ArchiveExtensions.Contains(Path.GetExtension(item).ToLowerInvariant()))
                continue;
            List<string> paths;
            try
            {
                paths = isDir
                    ? Directory.EnumerateFiles(item, "*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(item, f).Replace('/', '\\')).ToList()
                    : ArchiveFactory.OpenArchive(item).Entries.Where(e => !e.IsDirectory && e.Key is not null)
                        .Select(e => e.Key!.Replace('/', '\\')).ToList();
            }
            catch (Exception ex)
            {
                rows.Add($"{Path.GetFileName(item)}\t\tUNREADABLE\t{ex.Message}\t\t\t");
                continue;
            }

            var title = ImportPipeline.DeriveTitle(item);
            var exes = paths.Where(p => ExecutableExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var (cls, guess) = ImportPipeline.GuessExecutable(exes, title);
            var resolution = resolver.Resolve(new GameProfile { Title = title, Launch = new LaunchSpec { Executable = guess } }, paths);
            rows.Add(string.Join('\t', Path.GetFileName(item), title, cls, guess ?? "-",
                resolution.Recognized ? "yes" : "no", resolution.Executable ?? "-",
                resolution.Profile.Launch.Executable ?? "-"));
        }

        File.WriteAllLines(report, rows);
        Assert.True(rows.Count > 1);
    }
}
