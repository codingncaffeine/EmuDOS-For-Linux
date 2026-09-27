using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmuDOS.Core.Model;

namespace EmuDOS.Core.Catalog;

/// <summary>Result of building a catalog from its source list.</summary>
/// <param name="Entries">Games written.</param>
/// <param name="Revision">The catalog revision stamped into the file.</param>
/// <param name="Leaks">Source names found readable in the built file (must be empty).</param>
public sealed record CatalogBuildReport(int Entries, int Revision, IReadOnlyList<string> Leaks);

/// <summary>
/// Builds a catalog file from its readable source list — a JSON file kept OUT of the repository,
/// because it names games:
/// <code>{ "revision": 3, "entries": [ { "title": "…", "aliases": ["…"], "telltales": ["GAME.EXE"],
///   "launch": ["GAME.BAT", "GAME.EXE"], "profile": { "cpu": { "cyclesMode": "Fixed", "fixedCycles": 3000 } } } ] }</code>
/// <c>profile</c> is a partial <see cref="GameProfile"/> (camelCase, enum names as strings). After
/// writing, the file is scanned for every source name so a readable name can never ship.
/// </summary>
public static class CatalogSource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record SourceEntry(
        string Title,
        List<string>? Aliases,
        List<string>? Telltales,
        List<string>? Launch,
        GameProfile? Profile);

    private sealed record SourceFile(int Revision, List<SourceEntry> Entries);

    /// <summary>Build <paramref name="outputPath"/> (replacing it) from the source list at
    /// <paramref name="sourcePath"/>.</summary>
    public static CatalogBuildReport Build(string sourcePath, string outputPath)
    {
        var source = JsonSerializer.Deserialize<SourceFile>(File.ReadAllText(sourcePath), JsonOptions)
            ?? throw new InvalidDataException("The catalog source is empty.");
        if (source.Entries is null || source.Entries.Count == 0)
            throw new InvalidDataException("The catalog source has no entries.");

        var entries = new List<CatalogEntry>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in source.Entries)
        {
            if (string.IsNullOrWhiteSpace(e.Title))
                throw new InvalidDataException("A catalog entry has no title.");
            if (!ids.Add(e.Title))
                throw new InvalidDataException($"Duplicate catalog title: {e.Title}");
            entries.Add(new CatalogEntry
            {
                Id = e.Title,
                Title = e.Title,
                Aliases = e.Aliases ?? [],
                Telltales = e.Telltales ?? [],
                Launch = e.Launch ?? [],
                Profile = e.Profile ?? new GameProfile(),
            });
        }

        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
            File.Delete(outputPath + suffix);
        var db = new CatalogDatabase(outputPath);
        db.Build(entries, source.Revision);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // release the file before scanning it

        return new CatalogBuildReport(entries.Count, source.Revision, FindLeaks(outputPath, entries));
    }

    /// <summary>Every name of <paramref name="entries"/> that appears readable (case-insensitive,
    /// as UTF-8/ASCII or UTF-16) in the file at <paramref name="path"/>.</summary>
    public static IReadOnlyList<string> FindLeaks(string path, IEnumerable<CatalogEntry> entries)
    {
        var bytes = File.ReadAllBytes(path);
        var ascii = Encoding.Latin1.GetString(bytes).ToLowerInvariant();
        var utf16 = Encoding.Unicode.GetString(bytes.Length % 2 == 0 ? bytes : bytes[..^1]).ToLowerInvariant();

        var leaks = new List<string>();
        foreach (var entry in entries)
        {
            var names = new[] { entry.Title }.Concat(entry.Aliases).Concat(entry.Telltales).Concat(entry.Launch);
            foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
            {
                // The name as written and its normalised title form; very short forms are skipped
                // (they occur by chance in binary pages), which file names with an extension never are.
                var forms = new[] { name.Trim().ToLowerInvariant(), CatalogDatabase.NormalizeTitle(name) }
                    .Where(f => f.Length >= 5)
                    .Distinct();
                if (forms.Any(f => ascii.Contains(f, StringComparison.Ordinal) || utf16.Contains(f, StringComparison.Ordinal)))
                    leaks.Add(name);
            }
        }
        return leaks;
    }
}
