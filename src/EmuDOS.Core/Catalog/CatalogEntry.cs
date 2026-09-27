using EmuDOS.Core.Model;

namespace EmuDOS.Core.Catalog;

/// <summary>
/// One curated game in the catalog: a set of telltale filenames that identify it (Boxer's
/// approach), the names it goes by, the program that starts it, and the curated
/// <see cref="GameProfile"/> settings to apply when it is recognised. All names are hashed when the
/// catalog is built (see <see cref="CatalogHash"/>); only the settings are stored readable.
/// </summary>
public sealed record CatalogEntry
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Other names the game is imported under (matched exactly like <see cref="Title"/>).</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>Distinctive filenames whose presence identifies this game (whole names, matched
    /// case-insensitively in any folder of the content).</summary>
    public required IReadOnlyList<string> Telltales { get; init; }

    /// <summary>The program that starts the game, as file names in order of preference (e.g. a
    /// launcher batch before the raw executable). The first one present in the content is used.</summary>
    public IReadOnlyList<string> Launch { get; init; } = [];

    public required GameProfile Profile { get; init; }
}
