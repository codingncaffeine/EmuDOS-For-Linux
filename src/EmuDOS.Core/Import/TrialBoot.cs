namespace EmuDOS.Core.Import;

/// <summary>What a short trial boot of one candidate program showed.</summary>
public enum TrialOutcome
{
    /// <summary>Still running when the trial ended — plausibly the game.</summary>
    StillRunning,

    /// <summary>Went straight back to the DOS prompt (a usage message, "run SETUP first", a tool).</summary>
    ReturnedToDos,

    /// <summary>The trial could not run (no core, or the emulator failed); keep the guess.</summary>
    Unavailable,
}

/// <summary>
/// Boots a game's content headlessly for a moment with one candidate program, to tell a program that
/// runs from one that drops straight back to DOS. Import uses it when it had to guess the program
/// and there is more than one candidate.
/// </summary>
public interface ITrialBooter
{
    Task<TrialOutcome> BootAsync(string contentDir, string executable, CancellationToken cancellationToken = default);
}

/// <summary>The batch a trial boot runs and the marker file it leaves when the program returns.</summary>
public static class TrialBootScript
{
    /// <summary>File the trial batch writes in C:\ once the program has returned to DOS.</summary>
    public const string MarkerFile = "EMUTRY.TMP";

    /// <summary>
    /// DOSBOX.BAT for trying <paramref name="executable"/> (a content-relative DOS path): run it from
    /// its own folder (CALL for a batch, so control comes back), then leave the marker.
    /// </summary>
    public static string Build(string executable)
    {
        var exe = executable.Trim().Replace('/', '\\');
        int slash = exe.LastIndexOf('\\');
        var dir = slash >= 0 ? exe[..slash].Trim('\\') : string.Empty;
        var file = slash >= 0 ? exe[(slash + 1)..] : exe;
        var sb = new System.Text.StringBuilder();
        sb.Append("@ECHO OFF\r\n");
        if (dir.Length > 0)
            sb.Append("@CD \\").Append(dir).Append("\r\n");
        sb.Append(file.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ? "@CALL " : "@").Append(file).Append("\r\n");
        sb.Append("@ECHO.>C:\\").Append(MarkerFile).Append("\r\n");
        return sb.ToString();
    }
}
