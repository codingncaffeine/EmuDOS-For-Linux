namespace EmuDOS.Core.Import;

/// <summary>
/// Tells a folder of games from a single game, so a dropped collection imports each game on its
/// own. A folder is a collection when nothing at its top level runs (DOSBox's own launchers aside),
/// and at least two of its children are games — subfolders holding a DOS program or a disc image,
/// archives, or disc-image sets — none of them named like a part of one game (DOS, WIN, DATA,
/// DISK1, CD, …).
/// </summary>
public static class CollectionScanner
{
    private static readonly string[] ProgramExtensions = [".exe", ".com", ".bat"];
    private static readonly string[] ArchiveExtensions = [".zip", ".rar", ".7z"];
    private static readonly string[] DiscExtensions = [".iso", ".cue", ".chd", ".bin"];

    // Subfolders a single game is split into; a collection's subfolders are named after games.
    private static readonly HashSet<string> PartNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "c", "d", "dos", "win", "win31", "win95", "windows", "setup", "install", "installer", "data", "game",
        "bin", "exe", "cd", "cdrom", "cd1", "cd2", "cd3", "cd4", "disc", "disc1", "disc2", "disk", "disk1",
        "disk2", "disk3", "disk4", "disk5", "disks", "floppy", "floppies", "sound", "drivers", "utils",
        "utilities", "extras", "bonus", "manual", "manuals", "docs", "doc", "save", "saves", "patch", "patches",
        "update", "music", "movies", "video",
    };

    /// <summary>
    /// The paths to import for a drop: every dropped path, with collections replaced by their games
    /// (recursively, up to <paramref name="maxDepth"/> levels). Reads the folders — call it off the
    /// UI thread.
    /// </summary>
    public static IReadOnlyList<string> Expand(IEnumerable<string> paths, int maxDepth = 3)
    {
        var result = new List<string>();
        foreach (var path in paths)
            ExpandInto(path, maxDepth, result);
        return result;
    }

    /// <summary>The games directly inside <paramref name="dir"/> when it is a collection, else null.</summary>
    public static IReadOnlyList<string>? Items(string dir)
    {
        if (!Directory.Exists(dir))
            return null;
        try
        {
            var files = Directory.EnumerateFiles(dir).ToList();
            var dirs = Directory.EnumerateDirectories(dir).ToList();

            // Something at the top runs, or the folder carries a launch recipe: it is one game.
            if (files.Any(f => IsProgram(f) && !DosExecutables.IsRuntimeHelper(f)
                               && !DosExecutables.IsHostLauncherBatch(dir, Path.GetFileName(f)))
                || File.Exists(Path.Combine(dir, "AUTOBOOT.DBP"))
                || File.Exists(Path.Combine(dir, "DOSBOX.BAT")))
                return null;

            var programDirs = new List<string>();
            var discDirs = new List<string>();
            foreach (var sub in dirs)
            {
                var kind = Probe(sub);
                if (kind == ChildKind.Programs)
                    programDirs.Add(sub);
                else if (kind == ChildKind.Discs)
                    discDirs.Add(sub);
            }

            if (programDirs.Concat(discDirs).Any(d => PartNames.Contains(Path.GetFileName(d))))
                return null;

            var archives = files.Where(f => HasExtension(f, ArchiveExtensions)).ToList();
            var discs = ImportPipeline.WithoutCueTracks(files.Where(f => HasExtension(f, DiscExtensions))).ToList();
            var discSets = ImportPipeline.GroupDiscSets(discs).Count();

            var games = programDirs.Count + archives.Count + discSets;
            if (games >= 2 || (games == 0 && discDirs.Count >= 2))
                return [.. programDirs, .. discDirs, .. archives, .. discs];
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ExpandInto(string path, int depthLeft, List<string> result)
    {
        if (depthLeft > 0 && Items(path) is { } items)
        {
            foreach (var item in items)
                ExpandInto(item, depthLeft - 1, result);
            return;
        }
        result.Add(path);
    }

    private enum ChildKind { None, Programs, Discs }

    // What a subfolder holds, looking a few levels down: a DOS program makes it a game; a disc image
    // alone makes it a disc game; neither (art, manuals, ROMs) and it is not a game.
    private static ChildKind Probe(string dir)
    {
        var kind = ChildKind.None;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions
                     { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }))
            {
                if (IsProgram(f) && !DosExecutables.IsRuntimeHelper(f))
                    return ChildKind.Programs;
                if (HasExtension(f, DiscExtensions))
                    kind = ChildKind.Discs;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return kind;
    }

    private static bool IsProgram(string path) => HasExtension(path, ProgramExtensions);

    private static bool HasExtension(string path, string[] extensions) =>
        extensions.Contains(Path.GetExtension(path).ToLowerInvariant());
}
