using EmuDOS.Core.Model;

namespace EmuDOS.Core.Catalog;

/// <summary>How the catalog recognised a game.</summary>
public enum CatalogMatchKind
{
    /// <summary>Every telltale file of the entry is in the content.</summary>
    Files,

    /// <summary>The imported title equals one of the entry's names.</summary>
    Title,
}

/// <summary>A catalog hit: the curated settings plus the hashed launch programs, in preference order.</summary>
public sealed record CatalogMatch(GameProfile Profile, IReadOnlyList<string> LaunchKeys, CatalogMatchKind Kind)
{
    /// <summary>
    /// The first launch program of the entry that exists in the content, as the content-relative path
    /// given (either separator), or null when the content has none of them. When several files share
    /// the name, the shallowest one wins, then the first in ordinal order.
    /// </summary>
    public string? FindLaunch(IEnumerable<string> contentRelativePaths)
    {
        if (LaunchKeys.Count == 0)
            return null;

        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in contentRelativePaths
                     .OrderBy(Depth)
                     .ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var key = CatalogHash.Of(LaunchName(path));
            if (key.Length > 0)
                byKey.TryAdd(key, path);
        }

        foreach (var key in LaunchKeys)
            if (byKey.TryGetValue(key, out var path))
                return path;
        return null;
    }

    /// <summary>The normalised form a launch program is keyed by: its lowercase file name.</summary>
    public static string LaunchName(string path) => Leaf(path).Trim().ToLowerInvariant();

    // Content paths are DOS-style ("GAME\GAME.EXE") or host-style; take the leaf across both.
    internal static string Leaf(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut >= 0 ? path[(cut + 1)..] : path;
    }

    private static int Depth(string path) => path.Count(c => c is '\\' or '/');
}
