using System.IO.Compression;
using EmuDOS.Core.Catalog;
using EmuDOS.Core.Engine.DosBoxPure;
using EmuDOS.Core.Library;
using EmuDOS.Core.Model;

namespace EmuDOS.Core.Import;

/// <summary>What a finished session taught us about a game.</summary>
/// <param name="Program">The installed program to start from now on (content-relative for a folder
/// game, a <c>C:\</c> path on the persisted drive for a CD game).</param>
/// <param name="Recognized">The catalog recognised the installed game and named the program.</param>
/// <param name="Profile">The curated profile to adopt, when the catalog recognised a folder game.</param>
/// <param name="PinnedAutoBoot">The program was pinned as the CD game's auto-start.</param>
public sealed record InstallLearning(string Program, bool Recognized, GameProfile? Profile, bool PinnedAutoBoot);

/// <summary>
/// After a session, spots a game that got installed — new programs in a folder game's content (its
/// C: drive, written in place) or on a CD game's persisted C: drive — and picks the installed game
/// to start from then on: the catalog's program when it knows the game, else the best of the new
/// programs by the shared guesser. Setup and install programs never qualify, so a session that only
/// configured sound changes nothing.
/// </summary>
public static class InstallLearner
{
    private static readonly string[] ProgramExtensions = [".exe", ".com", ".bat"];

    /// <summary>Learn from the gamebox's state after a session; null when nothing new was
    /// installed or it was learned before. Reads (and for a CD game writes) the game's files — call
    /// it off the UI thread.</summary>
    public static InstallLearning? Learn(string contentDir, string saveDir, GameProfile profile,
                                         GameUserState state, ProfileResolver? resolver)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            return profile.SourceMedia == SourceMediaType.Iso
                ? LearnFromOverlay(saveDir, profile, resolver)
                : LearnFromContent(contentDir, saveDir, profile, state, resolver);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null; // a half-written save or an unreadable folder: try again after the next session
        }
    }

    private static InstallLearning? LearnFromContent(string contentDir, string saveDir, GameProfile profile,
                                                     GameUserState state, ProfileResolver? resolver)
    {
        // Programs the sessions created or changed since the content was first snapshotted.
        var produced = ContentBaseline.DiffSaves(contentDir, saveDir)
            .Where(IsProgram)
            .Select(p => p.Replace('/', '\\'))
            .ToList();
        if (produced.Count == 0)
            return null;

        // Learned from these same installed files before (a program a trial boot picked from the
        // original files at import is not; an install that happens later replaces it).
        if (state.LearnedExecutable is { } known && produced.Contains(known, StringComparer.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(contentDir, known.Replace('\\', '/'))))
            return null;

        var all = Directory.EnumerateFiles(contentDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(contentDir, f).Replace('/', '\\'))
            .ToList();
        if (resolver?.Resolve(profile with { Launch = profile.Launch with { Executable = null } }, all) is
            { Recognized: true, Executable: { } catalogProgram } resolution)
        {
            var adopted = profile.Origin == ProfileOrigin.UserOverride ? null : resolution.Profile;
            return new InstallLearning(catalogProgram, true, adopted, false);
        }

        var (classification, pick) = ExecutableGuesser.Guess(produced, profile.Title, contentDir);
        return classification == ImportClassification.ReadyToPlay && pick is not null
            ? new InstallLearning(pick, false, null, false)
            : null;
    }

    private static InstallLearning? LearnFromOverlay(string saveDir, GameProfile profile, ProfileResolver? resolver)
    {
        var zip = PureSave.FindSaveZip(saveDir);
        if (zip is null)
            return null;

        List<string> files;
        Dictionary<string, long> sizes;
        using (var archive = ZipFile.OpenRead(zip))
        {
            if (archive.GetEntry("AUTOBOOT.DBP") is not null)
                return null; // an auto-start is already pinned (by the user, the core, or us)
            var entries = archive.Entries.Where(e => e.Length > 0 && !e.FullName.EndsWith('/')).ToList();
            files = entries.Select(e => e.FullName.Replace('/', '\\')).ToList();
            sizes = entries.ToDictionary(e => e.FullName.Replace('/', '\\'), e => e.Length, StringComparer.OrdinalIgnoreCase);
        }

        var programs = files.Where(IsProgram).ToList();
        if (programs.Count == 0)
            return null;

        string? program = null;
        var recognized = false;
        if (resolver?.Resolve(profile with { Launch = new LaunchSpec() }, files) is
            { Recognized: true, Executable: { } catalogProgram })
        {
            program = catalogProgram;
            recognized = true;
        }
        else
        {
            var (classification, pick) = ExecutableGuesser.Guess(programs, profile.Title, sizes: sizes);
            if (classification == ImportClassification.ReadyToPlay)
                program = pick;
        }

        if (program is null)
            return null;
        var dosPath = @"C:\" + program;
        return PureSave.SetAutoBoot(saveDir, dosPath) ? new InstallLearning(dosPath, recognized, null, true) : null;
    }

    private static bool IsProgram(string path) =>
        ProgramExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
}
