using System.Text.Json;
using System.Text.Json.Serialization;
using EmuDOS.Core.Model;
using Microsoft.Data.Sqlite;

namespace EmuDOS.Core.Catalog;

/// <summary>Format, revision and size of a catalog file, read without opening it for writing.</summary>
public sealed record CatalogInfo(int Format, int Revision, int Entries);

/// <summary>
/// The curated config catalog: maps a game's telltale files (or its title) to its curated settings
/// and the program that starts it. Shipped as an embedded baseline and updatable as a download.
/// Matching follows Boxer — a game matches when ALL of an entry's telltales are present in the
/// content; the most specific (most telltales) wins. Every name is stored as a
/// <see cref="CatalogHash"/> key, never readable.
/// </summary>
public sealed class CatalogDatabase
{
    /// <summary>The on-disk format this build reads and writes (<c>PRAGMA user_version</c>). Format 1
    /// stored plain names and never shipped with content; it is rebuilt empty on open.</summary>
    public const int FormatVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _connectionString;

    public CatalogDatabase(string dbPath)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(dbPath))!);
        Path = dbPath;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var connection = Open();
        Initialize(connection);
    }

    /// <summary>The catalog file.</summary>
    public string Path { get; }

    /// <summary>Number of curated games.</summary>
    public int Count
    {
        get
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM CatalogEntries;";
            return (int)(long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>The catalog's revision (higher is newer; 0 when it has none).</summary>
    public int Revision
    {
        get
        {
            using var connection = Open();
            return ReadRevision(connection);
        }
    }

    /// <summary>
    /// (Re)build the catalog from entries (replaces any existing content). Titles, aliases, telltales
    /// and launch programs are stored as <see cref="CatalogHash"/> keys; the stored profile keeps only
    /// the settings (no title, launch line or mounts).
    /// </summary>
    public void Build(IEnumerable<CatalogEntry> entries, int revision = 0)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText =
                "DELETE FROM Telltales; DELETE FROM TitleKeys; DELETE FROM CatalogEntries; DELETE FROM Meta;";
            clear.ExecuteNonQuery();
        }

        foreach (var entry in entries)
        {
            var id = "e" + CatalogHash.Of("id:" + entry.Id)[..16];
            var names = new[] { entry.Title }.Concat(entry.Aliases).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            var launchKeys = entry.Launch
                .Select(n => CatalogHash.Of(CatalogMatch.LaunchName(n)))
                .Where(k => k.Length > 0)
                .Distinct()
                .ToList();
            var stored = entry.Profile with
            {
                Title = string.Empty,
                CanonicalId = null,
                Launch = new LaunchSpec(),
                Mounts = [],
                Origin = ProfileOrigin.CuratedBase,
            };

            using (var ins = connection.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT OR REPLACE INTO CatalogEntries (Id, Title, NormalizedTitle, NormalizedPrimary, ProfileJson, LaunchKeys)
                    VALUES ($id, '', $ntitle, $nprimary, $json, $launch);
                    """;
                ins.Parameters.AddWithValue("$id", id);
                ins.Parameters.AddWithValue("$ntitle", CatalogHash.Of(NormalizeTitle(entry.Title)));
                ins.Parameters.AddWithValue("$nprimary", CatalogHash.Of(NormalizeTitle(PrimaryTitle(entry.Title))));
                ins.Parameters.AddWithValue("$json", JsonSerializer.Serialize(stored, JsonOptions));
                ins.Parameters.AddWithValue("$launch", JsonSerializer.Serialize(launchKeys));
                ins.ExecuteNonQuery();
            }

            // Title keys: each name's full form (kind 0) and its part before a subtitle (kind 1).
            var titleKeys = new HashSet<(string Key, int Kind)>();
            foreach (var name in names)
            {
                var full = CatalogHash.Of(NormalizeTitle(name));
                var primary = CatalogHash.Of(NormalizeTitle(PrimaryTitle(name)));
                if (full.Length > 0)
                    titleKeys.Add((full, 0));
                if (primary.Length > 0 && primary != full)
                    titleKeys.Add((primary, 1));
            }
            foreach (var (key, kind) in titleKeys)
            {
                using var tk = connection.CreateCommand();
                tk.Transaction = tx;
                tk.CommandText = "INSERT INTO TitleKeys (EntryId, Key, Kind) VALUES ($id, $key, $kind);";
                tk.Parameters.AddWithValue("$id", id);
                tk.Parameters.AddWithValue("$key", key);
                tk.Parameters.AddWithValue("$kind", kind);
                tk.ExecuteNonQuery();
            }

            foreach (var telltale in entry.Telltales.Select(t => CatalogHash.Of(Normalize(t))).Where(k => k.Length > 0).Distinct())
            {
                using var tt = connection.CreateCommand();
                tt.Transaction = tx;
                tt.CommandText = "INSERT INTO Telltales (EntryId, FileName) VALUES ($id, $file);";
                tt.Parameters.AddWithValue("$id", id);
                tt.Parameters.AddWithValue("$file", telltale);
                tt.ExecuteNonQuery();
            }
        }

        using (var meta = connection.CreateCommand())
        {
            meta.Transaction = tx;
            meta.CommandText = "INSERT INTO Meta (Key, Value) VALUES ('format', $format), ('revision', $revision);";
            meta.Parameters.AddWithValue("$format", FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            meta.Parameters.AddWithValue("$revision", revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            meta.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Best catalog entry for a set of content filenames, or null if none matches.</summary>
    public CatalogMatch? Match(IEnumerable<string> contentFileNames)
    {
        var keys = contentFileNames
            .Select(n => CatalogHash.Of(Normalize(CatalogMatch.Leaf(n))))
            .Where(k => k.Length > 0)
            .Distinct()
            .ToList();
        if (keys.Count == 0)
            return null;

        using var connection = Open();
        using (var temp = connection.CreateCommand())
        {
            // IF NOT EXISTS + clear: pooled connections may carry the temp table over.
            temp.CommandText =
                "CREATE TEMP TABLE IF NOT EXISTS content (FileName TEXT PRIMARY KEY); DELETE FROM content;";
            temp.ExecuteNonQuery();
        }

        using (var tx = connection.BeginTransaction())
        {
            using var ins = connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO content (FileName) VALUES ($f);";
            var p = ins.Parameters.Add("$f", SqliteType.Text);
            foreach (var key in keys)
            {
                p.Value = key;
                ins.ExecuteNonQuery();
            }

            tx.Commit();
        }

        // Entries whose every telltale is present, most specific first.
        string? entryId;
        using (var q = connection.CreateCommand())
        {
            q.CommandText = """
                SELECT t.EntryId
                FROM Telltales t
                LEFT JOIN content c ON c.FileName = t.FileName
                GROUP BY t.EntryId
                HAVING COUNT(*) = SUM(CASE WHEN c.FileName IS NOT NULL THEN 1 ELSE 0 END)
                ORDER BY COUNT(*) DESC, t.EntryId
                LIMIT 1;
                """;
            entryId = q.ExecuteScalar() as string;
        }

        return entryId is null ? null : LoadMatch(connection, entryId, CatalogMatchKind.Files);
    }

    /// <summary>
    /// Catalog entry for a game title (normalized), or null. The fallback when the content's files
    /// can't identify the game (an installer-only copy, a repack with renamed files).
    /// </summary>
    public CatalogMatch? MatchByTitle(string title)
    {
        var full = CatalogHash.Of(NormalizeTitle(title));
        if (full.Length == 0)
            return null;
        var primary = CatalogHash.Of(NormalizeTitle(PrimaryTitle(title)));

        using var connection = Open();
        string? entryId;
        using (var cmd = connection.CreateCommand())
        {
            // Prefer a whole-title hit over a subtitle-stripped one, on either side.
            cmd.CommandText = """
                SELECT EntryId FROM TitleKeys
                WHERE Key = $full OR ($primary <> '' AND Key = $primary)
                ORDER BY (CASE WHEN Key = $full THEN 0 ELSE 2 END) + Kind, EntryId
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$full", full);
            cmd.Parameters.AddWithValue("$primary", primary);
            entryId = cmd.ExecuteScalar() as string;
        }

        return entryId is null ? null : LoadMatch(connection, entryId, CatalogMatchKind.Title);
    }

    /// <summary>Format, revision and entry count of a catalog file, or null when it is missing or
    /// not a readable catalog. Opens read-only and unpooled, so it never creates or holds the file.</summary>
    public static CatalogInfo? ReadInfo(string dbPath)
    {
        if (!File.Exists(dbPath))
            return null;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            int format;
            using (var v = connection.CreateCommand())
            {
                v.CommandText = "PRAGMA user_version;";
                format = (int)(long)(v.ExecuteScalar() ?? 0L);
            }
            if (format != FormatVersion)
                return new CatalogInfo(format, 0, 0);

            int entries;
            using (var c = connection.CreateCommand())
            {
                c.CommandText = "SELECT COUNT(*) FROM CatalogEntries;";
                entries = (int)(long)(c.ExecuteScalar() ?? 0L);
            }
            return new CatalogInfo(format, ReadRevision(connection), entries);
        }
        catch (SqliteException)
        {
            return null; // not a database, or not a catalog
        }
    }

    /// <summary>
    /// Collapse a title to a comparison key: lowercase, drop year/articles/punctuation, and turn
    /// multi-letter roman numerals into digits so "Game II" and "Game 2" land on the same key.
    /// </summary>
    public static string NormalizeTitle(string title)
    {
        var s = (title ?? string.Empty).ToLowerInvariant();
        s = YearRegex.Replace(s, " ");
        s = RomanRegex.Replace(s, m => RomanToArabic(m.Value));
        s = ArticleRegex.Replace(s, " ");
        s = NoiseWordRegex.Replace(s, " "); // "episode 1" -> "1", etc.
        return NonAlnumRegex.Replace(s, string.Empty);
    }

    /// <summary>The part of a title before its subtitle (" - " or ": "), for looser matching.</summary>
    public static string PrimaryTitle(string title)
    {
        if (string.IsNullOrEmpty(title))
            return title ?? string.Empty;
        int dash = title.IndexOf(" - ", StringComparison.Ordinal);
        int colon = title.IndexOf(": ", StringComparison.Ordinal);
        int cut = dash >= 0 && (colon < 0 || dash < colon) ? dash : colon;
        return cut > 0 ? title[..cut] : title;
    }

    private static string RomanToArabic(string roman) => roman switch
    {
        "ii" => "2", "iii" => "3", "iv" => "4", "vi" => "6", "vii" => "7",
        "viii" => "8", "ix" => "9", "xi" => "11", "xii" => "12", "xiii" => "13",
        _ => roman,
    };

    private static readonly System.Text.RegularExpressions.Regex YearRegex =
        new(@"\(?\b(19|20)\d{2}\b\)?", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Multi-letter romans only — single i/v/x are too ambiguous (e.g. "Mega Man X").
    private static readonly System.Text.RegularExpressions.Regex RomanRegex =
        new(@"\b(xiii|xii|xi|viii|vii|vi|ix|iv|iii|ii)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ArticleRegex =
        new(@"\b(the|a|an)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex NoiseWordRegex =
        new(@"\b(episode|ep|part|chapter|volume|vol|disk|disc)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex NonAlnumRegex =
        new(@"[^a-z0-9]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static CatalogMatch? LoadMatch(SqliteConnection connection, string entryId, CatalogMatchKind kind)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ProfileJson, LaunchKeys FROM CatalogEntries WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", entryId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        var profile = JsonSerializer.Deserialize<GameProfile>(reader.GetString(0), JsonOptions);
        if (profile is null)
            return null;
        var launch = JsonSerializer.Deserialize<List<string>>(reader.GetString(1)) ?? [];
        return new CatalogMatch(profile, launch, kind);
    }

    private static int ReadRevision(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Meta WHERE Key = 'revision';";
        return cmd.ExecuteScalar() is string s
               && int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var r)
            ? r
            : 0;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void Initialize(SqliteConnection connection)
    {
        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "PRAGMA user_version;";
        if ((long)(versionCmd.ExecuteScalar() ?? 0L) >= FormatVersion)
            return;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $$"""
            DROP TABLE IF EXISTS Telltales;
            DROP TABLE IF EXISTS TitleKeys;
            DROP TABLE IF EXISTS CatalogEntries;
            DROP TABLE IF EXISTS Meta;

            CREATE TABLE CatalogEntries (
                Id                TEXT PRIMARY KEY,
                Title             TEXT NOT NULL,
                NormalizedTitle   TEXT NOT NULL,
                NormalizedPrimary TEXT NOT NULL,
                ProfileJson       TEXT NOT NULL,
                LaunchKeys        TEXT NOT NULL DEFAULT '[]'
            );

            CREATE TABLE Telltales (
                EntryId  TEXT NOT NULL,
                FileName TEXT NOT NULL,
                FOREIGN KEY(EntryId) REFERENCES CatalogEntries(Id) ON DELETE CASCADE
            );

            CREATE TABLE TitleKeys (
                EntryId TEXT NOT NULL,
                Key     TEXT NOT NULL,
                Kind    INTEGER NOT NULL,
                FOREIGN KEY(EntryId) REFERENCES CatalogEntries(Id) ON DELETE CASCADE
            );

            CREATE TABLE Meta (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE INDEX idx_telltale_file ON Telltales(FileName);
            CREATE INDEX idx_titlekey ON TitleKeys(Key);

            PRAGMA user_version = {{FormatVersion}};
            """;
        cmd.ExecuteNonQuery();
    }

    // Telltales match by the whole lowercase file name, extension included: a curated entry names real
    // files, and a bare stem would let a generic data file (SKY.PCX, MAIN.DAT) identify the wrong game.
    private static string Normalize(string fileName) => CatalogMatch.LaunchName(fileName);
}
