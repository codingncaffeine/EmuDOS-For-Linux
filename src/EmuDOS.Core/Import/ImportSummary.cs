namespace EmuDOS.Core.Import;

/// <summary>
/// The status line an import leaves behind: whether the catalog recognised the game and which
/// program it will start, so the user knows when "Choose program…" is needed.
/// </summary>
public static class ImportSummary
{
    /// <summary>One line for one imported game.</summary>
    public static string Describe(ImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var title = string.IsNullOrWhiteSpace(result.Title) ? "the game" : result.Title;
        if (result.Warning is not null)
            return result.Warning;

        var program = Leaf(result.ChosenExecutable);
        var recognised = result.CatalogRecognized ? "recognised by the catalog" : "not in the catalog";
        return (result.Classification, result.LaunchSource) switch
        {
            (ImportClassification.ReadyToPlay, LaunchSource.Catalog) =>
                $"Imported {title} — recognised by the catalog; it runs {program}.",
            (ImportClassification.ReadyToPlay, LaunchSource.PackageScript) =>
                $"Imported {title} — {recognised}; it runs the launch script that came with it.",
            (ImportClassification.ReadyToPlay, LaunchSource.AutoBoot) =>
                $"Imported {title} — {recognised}; it runs its installed program {program}.",
            (ImportClassification.ReadyToPlay, _) when program is not null =>
                $"Imported {title} — {recognised}; guessed {program} (use Choose program… if that's wrong).",
            (ImportClassification.NeedsInstall, _) when program is not null =>
                $"Imported {title} — {recognised}; it needs installing: open it to run {program}.",
            (ImportClassification.NeedsInstall, _) =>
                $"Imported {title} — open it to install (the disc is mounted as D:).",
            _ => $"Imported {title} — no program found; open it to reach the DOS prompt.",
        };
    }

    /// <summary>One line for a batch of imported games.</summary>
    public static string Describe(IReadOnlyList<ImportResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 1)
            return Describe(results[0]);

        var recognised = results.Count(r => r.CatalogRecognized);
        var guessed = results.Where(r => r.LaunchSource == LaunchSource.Guess
                                         && r.Classification == ImportClassification.ReadyToPlay).ToList();
        var install = results.Count(r => r.Classification == ImportClassification.NeedsInstall);
        var parts = new List<string> { $"{recognised} recognised by the catalog" };
        if (guessed.Count == 1)
            parts.Add($"1 guessed ({guessed[0].Title} runs {Leaf(guessed[0].ChosenExecutable)})");
        else if (guessed.Count > 1)
            parts.Add($"{guessed.Count} guessed (use Choose program… on any that start the wrong program)");
        if (install > 0)
            parts.Add($"{install} to install");
        return $"Imported {results.Count} games — {string.Join(", ", parts)}.";
    }

    private static string? Leaf(string? path) =>
        path is null ? null : path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
}
