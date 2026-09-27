namespace EmuDOS.Core.Import;

/// <summary>How a freshly imported gamebox looks to us.</summary>
public enum ImportClassification
{
    /// <summary>A runnable game executable was found.</summary>
    ReadyToPlay,

    /// <summary>Only an installer was found — the game must be installed first.</summary>
    NeedsInstall,

    /// <summary>No executable found (e.g. raw disk images, or content we don't understand yet).</summary>
    Unknown,
}

/// <summary>Where an imported game's launch program came from (said in the import status line).</summary>
public enum LaunchSource
{
    /// <summary>Nothing to run yet: a disc to install from, or content we could not read.</summary>
    None,

    /// <summary>The curated catalog named the program.</summary>
    Catalog,

    /// <summary>Picked by the executable heuristics (title-named, known launcher, …).</summary>
    Guess,

    /// <summary>The package's own DOSBOX.BAT launch recipe.</summary>
    PackageScript,

    /// <summary>The target an installed game pinned in AUTOBOOT.DBP.</summary>
    AutoBoot,
}

/// <summary>Progress of an import (extraction is the slow part).</summary>
public readonly record struct ImportProgress(string Stage, double? Fraction);

/// <summary>Outcome of importing a folder/archive into a gamebox.</summary>
public sealed record ImportResult
{
    public required bool Success { get; init; }

    public string? GameboxPath { get; init; }

    public ImportClassification Classification { get; init; }

    /// <summary>All executables found in the content, as paths relative to the content root.</summary>
    public IReadOnlyList<string> Executables { get; init; } = [];

    /// <summary>The executable we'd launch (or the installer to run, when NeedsInstall).</summary>
    public string? ChosenExecutable { get; init; }

    public string? Error { get; init; }

    /// <summary>The imported game's title (derived from the dropped folder or archive name).</summary>
    public string? Title { get; init; }

    /// <summary>The curated catalog recognised the game and applied its settings.</summary>
    public bool CatalogRecognized { get; init; }

    /// <summary>Where <see cref="ChosenExecutable"/> (or the launch recipe) came from.</summary>
    public LaunchSource LaunchSource { get; init; }

    /// <summary>A non-fatal note to surface to the user (e.g. an unsupported disc format).</summary>
    public string? Warning { get; init; }
}
