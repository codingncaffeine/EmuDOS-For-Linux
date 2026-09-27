using EmuDOS.Core.Model;

namespace EmuDOS.Core.Catalog;

/// <summary>What the catalog made of an imported game.</summary>
/// <param name="Profile">The effective profile (the baseline when nothing matched).</param>
/// <param name="Recognized">The catalog recognised the game and its settings were applied.</param>
/// <param name="Executable">The catalog's launch program found in the content, or null.</param>
public sealed record CatalogResolution(GameProfile Profile, bool Recognized, string? Executable);

/// <summary>
/// Decides the effective profile for a game by layering: a curated catalog match provides the
/// base config and the program to start, the imported baseline keeps the game's identity, and a
/// user override (once the user edits) locks the profile so auto-enrichment never clobbers their
/// changes.
/// </summary>
public sealed class ProfileResolver(CatalogDatabase catalog)
{
    /// <summary>
    /// Enrich an imported <paramref name="baseline"/> with curated config when the catalog
    /// recognizes the content. <paramref name="contentPaths"/> are the content's files, relative to
    /// its root (DOS or host separators). A profile the user has taken over is returned untouched.
    /// </summary>
    public CatalogResolution Resolve(GameProfile baseline, IReadOnlyCollection<string> contentPaths)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(contentPaths);

        // The user has the final say — never overwrite their edits.
        if (baseline.Origin == ProfileOrigin.UserOverride)
            return new CatalogResolution(baseline, false, null);

        if (Find(contentPaths, baseline.Title) is not { } match)
            return new CatalogResolution(baseline, false, null);

        // Curated settings win; the gamebox keeps its own identity, mounts and launch recipe, except
        // that the catalog's program replaces the guessed one when the content has it.
        var exe = match.FindLaunch(contentPaths);
        var launch = exe is null ? baseline.Launch : baseline.Launch with { Executable = exe };
        var profile = match.Profile with
        {
            Title = baseline.Title,
            CanonicalId = baseline.CanonicalId,
            SourceMedia = baseline.SourceMedia,
            Mounts = baseline.Mounts,
            Launch = launch,
            Origin = ProfileOrigin.CuratedBase,
        };
        return new CatalogResolution(profile, true, exe);
    }

    /// <summary>
    /// The catalog's launch program for a game folder, as a content-relative DOS path, or null when
    /// the catalog does not know the game or the content lacks its program. Reads the folder; call
    /// it off the UI thread.
    /// </summary>
    public string? LaunchExecutable(string contentDir, string title)
    {
        if (!Directory.Exists(contentDir))
            return null;
        List<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(contentDir, "*", SearchOption.AllDirectories)
                .Select(f => System.IO.Path.GetRelativePath(contentDir, f).Replace('/', '\\'))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Find(paths, title)?.FindLaunch(paths);
    }

    // Prefer a content (telltale) match; fall back to the game's title. A damaged or unreadable
    // catalog must never break an import or a launch, so it reads as "not recognised".
    private CatalogMatch? Find(IReadOnlyCollection<string> contentPaths, string title)
    {
        try
        {
            return catalog.Match(contentPaths) ?? catalog.MatchByTitle(title);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }
}
