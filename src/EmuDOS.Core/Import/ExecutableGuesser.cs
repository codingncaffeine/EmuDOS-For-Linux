namespace EmuDOS.Core.Import;

/// <summary>
/// The one executable guesser, shared by import and launch: which program in a game's content is
/// the game, when neither the user nor the catalog has said. Import and launch used to guess with
/// different rules, so the program import reported was not always the one launch ran.
/// </summary>
public static class ExecutableGuesser
{
    // Graphics-mode builds of one program (GAMEEGA/GAMECGA next to GAME, VGAGAME next to GAME…): the
    // plain one is the loader that picks among them.
    private static readonly string[] GraphicsTags = ["mcga", "cga", "ega", "vga", "tga", "hgc", "herc", "tandy", "tan"];

    /// <summary>
    /// Classify the content and pick the program to run. <paramref name="executables"/> are the
    /// content's .exe/.com/.bat files as content-relative DOS paths. <paramref name="contentDir"/>,
    /// when given, supplies file sizes and lets the guess skip batch files that start DOSBox
    /// themselves; <paramref name="sizes"/> supplies sizes when the files are not on disk (an archive
    /// listing).
    /// </summary>
    /// <remarks>
    /// Games first (everything but setup/installer programs, runtime tools and host launchers):
    /// 1. a program named after the title (strongest match, then shallowest, a batch before the
    ///    program beside it, then the largest);
    /// 2. a well-known launcher name (RUN, START, PLAY, …);
    /// 3. when a DOS extender ships with the game, a batch named like the main program (no deeper
    ///    than it) that starts it;
    /// 4. the largest real program that isn't a bundled utility or a graphics-mode variant;
    /// 5. any other batch, then the utilities.
    /// With only utilities and an installer, the installer (INSTALL before SETUP, shallowest first)
    /// and NeedsInstall.
    /// </remarks>
    public static (ImportClassification Classification, string? Executable) Guess(
        IReadOnlyList<string> executables, string title, string? contentDir = null,
        IReadOnlyDictionary<string, long>? sizes = null)
    {
        ArgumentNullException.ThrowIfNull(executables);
        long SizeOf(string relPath) => Size(contentDir, sizes, relPath);

        var usable = executables
            .Where(e => !DosExecutables.IsRuntimeHelper(e))
            .Where(e => contentDir is null || !DosExecutables.IsHostLauncherBatch(contentDir, e))
            .ToList();

        // A program named exactly like a title word is the game even if it looks like a setup tool.
        var games = usable
            .Where(e => !DosExecutables.IsSetupLike(e) || DosExecutables.TitleMatchStrength(e, title) == 3)
            .ToList();
        var installers = usable.Except(games).ToList();

        var real = games.Where(g => !DosExecutables.IsLikelyUtility(g)).ToList();
        if (real.Count > 0 || (games.Count > 0 && installers.Count == 0))
            return (ImportClassification.ReadyToPlay, PickGame(games, real, title, executables, SizeOf));

        if (installers.Count > 0)
            return (ImportClassification.NeedsInstall, PickInstaller(installers));

        return (ImportClassification.Unknown, usable.FirstOrDefault() ?? executables.FirstOrDefault());
    }

    private static string PickGame(List<string> games, List<string> real, string title,
                                   IReadOnlyList<string> all, Func<string, long> sizeOf)
    {
        var titled = games
            .Select(g => (Path: g, Strength: DosExecutables.TitleMatchStrength(g, title)))
            .Where(x => x.Strength > 0)
            .OrderByDescending(x => x.Strength)
            .ThenBy(x => Depth(x.Path))
            .ThenBy(x => IsSidelined(x.Path))
            .ThenByDescending(x => IsBatch(x.Path))
            .ThenByDescending(x => sizeOf(x.Path))
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Path)
            .FirstOrDefault();
        if (titled is not null)
            return titled;

        var known = Shallowest(games.Where(DosExecutables.IsKnownLauncher));
        if (known is not null)
            return known;

        var programs = WithoutGraphicsVariants(real.Where(g => !IsBatch(g)).ToList())
            .OrderByDescending(sizeOf)
            .ThenBy(Depth)
            .ThenBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A DOS-extender game is often started by a batch named like its program (GAMEDOS.BAT for
        // GAME24.EXE, GAME.BAT for GAME.EXE) that loads drivers or picks a build first.
        if (all.Any(DosExecutables.IsExtender)
            && programs.FirstOrDefault() is { } main
            && Shallowest(real.Where(g => IsBatch(g) && Depth(g) <= Depth(main) && SharesPrefix(Stem(g), Stem(main)))) is { } launcher)
            return launcher;

        return programs.FirstOrDefault()
            ?? Shallowest(real)
            ?? games.OrderByDescending(sizeOf).ThenBy(Depth).ThenBy(g => g, StringComparer.OrdinalIgnoreCase).First();
    }

    private static string PickInstaller(List<string> installers) =>
        installers
            .OrderBy(i => Stem(i) == "install" ? 0 : Stem(i).Contains("install", StringComparison.Ordinal) ? 1 : 2)
            .ThenBy(Depth)
            .ThenBy(IsBatch)
            .ThenBy(i => i, StringComparer.OrdinalIgnoreCase)
            .First();

    // Drop GAMEEGA/EGAGAME-style builds when the plain GAME sits in the same folder.
    private static IEnumerable<string> WithoutGraphicsVariants(List<string> programs)
    {
        var byFolder = programs.ToLookup(Folder, StringComparer.OrdinalIgnoreCase);
        return programs.Where(p =>
        {
            var stem = Stem(p);
            var siblings = byFolder[Folder(p)].Select(Stem).ToHashSet();
            return !GraphicsTags.Any(tag =>
                (stem.Length > tag.Length + 1 && stem.EndsWith(tag, StringComparison.Ordinal) && siblings.Contains(stem[..^tag.Length]))
                || (stem.Length > tag.Length + 1 && stem.StartsWith(tag, StringComparison.Ordinal) && siblings.Contains(stem[tag.Length..])));
        });
    }

    // "_GAME.BAT", "!start.bat": helper or backup copies that sort behind the plain name.
    private static bool IsSidelined(string path) => Stem(path) is ['_' or '!', ..];

    private static bool SharesPrefix(string a, string b) =>
        a.Length >= 2 && b.Length >= 2
        && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)
            || (a.Length >= 3 && b.Length >= 3 && a[..3] == b[..3]));

    private static string? Shallowest(IEnumerable<string> paths) =>
        paths.OrderBy(Depth).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    private static bool IsBatch(string path) => path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    private static int Depth(string path) => path.Count(c => c is '\\' or '/');

    private static string Folder(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut >= 0 ? path[..cut] : string.Empty;
    }

    private static string Stem(string path)
    {
        var leaf = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        var dot = leaf.LastIndexOf('.');
        return (dot > 0 ? leaf[..dot] : leaf).ToLowerInvariant();
    }

    private static long Size(string? contentDir, IReadOnlyDictionary<string, long>? sizes, string relPath)
    {
        if (sizes is not null)
            return sizes.TryGetValue(relPath, out var known) ? known : 0;
        if (contentDir is null)
            return 0;
        try
        {
            var file = new FileInfo(Path.Combine(contentDir, relPath.Replace('\\', '/')));
            return file.Exists ? file.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }
}
