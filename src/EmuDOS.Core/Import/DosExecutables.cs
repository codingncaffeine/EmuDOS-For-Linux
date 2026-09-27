namespace EmuDOS.Core.Import;

/// <summary>
/// Classifies DOS executables that are NOT a game's launch target: DOS extenders (DOS4GW and
/// friends — runtime helpers a .bat invokes) and emulator wrappers. Used so import doesn't guess
/// an extender as the program to run, and so the Run menu doesn't list noise.
/// </summary>
public static class DosExecutables
{
    private static readonly string[] Extenders =
        ["dos4gw", "dos4g", "dos32a", "dos32", "pmodew", "pmode", "cwsdpmi", "dpmi", "dpmiload", "rtm"];

    private static readonly string[] Wrappers = ["dosbox", "4dos"];

    // Tools an installer or a sound/memory setup runs — archive extractors, copiers, driver and
    // patch-set loaders. Never the game, and never worth offering as the thing to launch.
    private static readonly string[] Tools =
    [
        "deice", "pkunzip", "pkunzjr", "pkzip", "pkxarc", "lha", "lharc", "arj", "unarj", "unzip",
        "mpscopy", "loadpats", "ultramid", "ultrinit", "mscdex", "smartdrv", "himem", "emm386", "ctmouse", "mouse",
        "univbe", "univesa", "imuse", "command", "keyb", "doskey", "share",
    ];

    // Filenames that are the canonical launch target across many games, so prefer them as the
    // default when present — even over the largest-exe / extender-.bat guesses. Sierra SCI games
    // boot via SIERRA.EXE (older ones via SCIV/SCIW/SCIDHUV); repackaged sets commonly ship a
    // run/start/play/go launcher.
    private static readonly string[] Launchers =
        ["sierra", "sciv", "sciw", "scidhuv", "run", "runme", "start", "play", "game", "go"];

    // Support tools that ship alongside a game but are never the game itself. Used to push them
    // below real candidates when guessing the launch target (e.g. a big "DVD Prep Wizard.exe"
    // shouldn't outweigh SIERRA.EXE just because it's larger).
    private static readonly string[] UtilityMarkers =
    [
        "wizard", "prep", "patch", "unins", "regist", "order", "help", "readme", "manual", "demo",
        "boot", "info", "catalog", "cdplay", "detect", "autodet", "intro", "trainer", "vesa", "view",
        "edit", "ipx", "modem", "dwango", "kali", "sound", "chat",
    ];

    // A configuration or installation program: never the game itself. Matched inside the name, since
    // these come as _INSTALL, INSTALLH, SNDSETUP, GAMEINST, DOSCONFIG and the like.
    private static readonly string[] SetupMarkers = ["setup", "install", "config", "setting"];

    /// <summary>
    /// Follow a launcher <c>.bat</c> that just runs an exe by a hardcoded path to the real exe in the
    /// content, when that path doesn't resolve (common in packaged/eXoDOS games whose .bat assumes a
    /// fixed install drive/folder like <c>C:\TOMBRAID\TOMB.EXE</c>, which breaks once the game is
    /// imported into a subfolder). Returns the content-relative path of the real exe, or the original
    /// path unchanged when the .bat isn't a broken redirector (its target resolves, it does real
    /// SET/PATH/MOUNT setup, or the exe can't be found). Lets such games launch with no manual fixup.
    /// </summary>
    public static string ResolveBatRedirect(string contentDir, string relPath)
    {
        if (string.IsNullOrEmpty(relPath) || !relPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            return relPath;
        try
        {
            var lines = File.ReadLines(Path.Combine(contentDir, relPath))
                .Select(l => l.Trim().TrimStart('@').Trim())
                .Where(l => l.Length > 0
                         && !l.StartsWith("echo", StringComparison.OrdinalIgnoreCase)
                         && !l.StartsWith("rem", StringComparison.OrdinalIgnoreCase)
                         && !l.StartsWith("::"))
                .ToList();

            // A .bat that sets up the environment or mounts is a genuine launcher — leave it alone.
            if (lines.Any(l => l.StartsWith("set ", StringComparison.OrdinalIgnoreCase)
                            || l.StartsWith("path ", StringComparison.OrdinalIgnoreCase)
                            || l.Contains("mount", StringComparison.OrdinalIgnoreCase)))
                return relPath;

            var target = lines.Select(ExeToken).FirstOrDefault(t => t is not null);
            if (target is null)
                return relPath;

            // Normalise to '/' (which .NET path APIs accept on every OS); a DOS '\' separator isn't
            // a separator to Path.Combine/GetFileName on Linux, so File lookups below would miss.
            var dosPath = target.Replace('\\', '/').TrimStart('/');
            if (dosPath.Length > 1 && dosPath[1] == ':')   // strip a drive letter (C:\…)
                dosPath = dosPath[2..].TrimStart('/');

            if (File.Exists(Path.Combine(contentDir, dosPath)))
                return relPath; // the hardcoded path is valid here — the .bat is fine

            var realExe = Directory
                .EnumerateFiles(contentDir, Path.GetFileName(dosPath), SearchOption.AllDirectories)
                .FirstOrDefault();
            return realExe is null ? relPath : Path.GetRelativePath(contentDir, realExe).Replace('/', '\\');
        }
        catch
        {
            return relPath;
        }
    }

    // The first .exe/.com token on a line (ignoring args / leading commands like CALL).
    private static string? ExeToken(string line) =>
        line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(t => t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                              || t.EndsWith(".com", StringComparison.OrdinalIgnoreCase));

    /// <summary>A DOS extender (the game's .bat launcher runs it; it's never the launch target).</summary>
    public static bool IsExtender(string path) => Extenders.Contains(Stem(path));

    /// <summary>A well-known canonical launcher filename — the best default when one is present.</summary>
    public static bool IsKnownLauncher(string path) => Launchers.Contains(Stem(path));

    /// <summary>Looks like a bundled support tool (patcher, registration, readme viewer …), so it's
    /// a poor launch guess — deprioritise it behind real candidates.</summary>
    public static bool IsLikelyUtility(string path)
    {
        var stem = Stem(path);
        return UtilityMarkers.Any(m => stem.Contains(m))
            // A Windows build shipped beside the DOS one (GAME95, WINGAME, GAMEWIN).
            || stem.EndsWith("95", StringComparison.Ordinal) || stem.StartsWith("win", StringComparison.Ordinal)
            || stem.EndsWith("win", StringComparison.Ordinal);
    }

    private static readonly string[] FillerWords = ["the", "of", "and", "a", "an", "to", "in"];

    /// <summary>Whether an executable's filename plausibly names the game (see
    /// <see cref="TitleMatchStrength"/>).</summary>
    public static bool TitleMatches(string fileName, string title) => TitleMatchStrength(fileName, title) > 0;

    /// <summary>How strongly an executable's filename names the game, allowing for the common DOS
    /// abbreviations: 3 = an exact title word (or the whole title), 2 = the title's initials, 1 = a
    /// substring either way or the first title word as a prefix (an abbreviated or truncated name),
    /// 0 = no match. Length-guarded so short coincidences don't match. Deterministic — no fuzzy
    /// distance.</summary>
    public static int TitleMatchStrength(string fileName, string title)
    {
        static string Norm(string s) =>
            new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        var stem = Norm(LeafWithoutExtension(fileName));
        if (stem.Length < 2)
            return 0;

        var words = (title ?? string.Empty)
            .Split([' ', '_', '-', '.', '(', ')', '[', ']', ':', '\'', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(Norm)
            .Where(w => w.Length > 0 && !FillerWords.Contains(w))
            .ToList();
        if (words.Count == 0)
            return 0;

        var key = string.Concat(words);
        if (words.Contains(stem) || stem == key)
            return 3; // an exact title word, or the whole title

        var acronym = string.Concat(words.Select(w => w[0]));
        if (acronym.Length >= 3 && stem == acronym)
            return 2; // the title's initials

        if (key.Length >= 3 && stem.Length >= 3 && (stem.Contains(key) || key.Contains(stem)))
            return 1; // substring either way (exe name contains, or is contained by, the title)

        return words[0].Length >= 4 && stem.StartsWith(words[0], StringComparison.Ordinal) ? 1 : 0;
        // the first title word as a prefix (an abbreviated/truncated exe name)
    }

    /// <summary>An extender, emulator wrapper or installer/driver tool — never a launch target.</summary>
    public static bool IsRuntimeHelper(string path)
    {
        var stem = Stem(path);
        return Extenders.Contains(stem) || Wrappers.Contains(stem) || Tools.Contains(stem);
    }

    /// <summary>A setup, installer or configuration program (SETUP, INSTALL, _INSTALL.BAT, GAMEINST,
    /// CONFIG, SETTINGS, …): not the game, though it may be what an installer-only copy runs first.</summary>
    public static bool IsSetupLike(string path)
    {
        var stem = Stem(path);
        return SetupMarkers.Any(m => stem.Contains(m, StringComparison.Ordinal))
            || stem.StartsWith("inst", StringComparison.Ordinal)
            || stem.EndsWith("inst", StringComparison.Ordinal);
    }

    /// <summary>A batch file that starts DOSBox itself (a Windows or Linux host launcher shipped next
    /// to a repacked game, e.g. <c>dosbox -conf dosbox.conf</c>): it cannot run inside the emulator.
    /// Reads the file; false when it cannot be read.</summary>
    public static bool IsHostLauncherBatch(string contentDir, string relPath)
    {
        if (!relPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var file = new FileInfo(Path.Combine(contentDir, relPath.Replace('\\', '/')));
            if (!file.Exists || file.Length > 8192)
                return false;
            foreach (var raw in File.ReadLines(file.FullName))
            {
                var line = raw.Trim().TrimStart('@').Trim();
                if (line.Length == 0 || line.StartsWith("echo", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("rem", StringComparison.OrdinalIgnoreCase) || line.StartsWith("::"))
                    continue;
                if (line.Split([' ', '\t', '"'], StringSplitOptions.RemoveEmptyEntries).Any(t => Stem(t) == "dosbox"))
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Stem(string path) =>
        LeafWithoutExtension(path).ToLowerInvariant();

    // Relative executable paths are DOS-style ("GTADOS\GTA.EXE"). Path.GetFileName* on Linux don't
    // treat '\' as a separator, so collapse it to '/' before taking the leaf name.
    private static string LeafWithoutExtension(string path) =>
        Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
}
