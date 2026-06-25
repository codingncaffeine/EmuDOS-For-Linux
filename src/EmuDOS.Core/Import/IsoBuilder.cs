using System.Diagnostics;

namespace EmuDOS.Core.Import;

/// <summary>
/// Builds a standard ISO9660 + Joliet CD image from a folder by shelling out to <c>xorriso</c>
/// (falling back to <c>genisoimage</c> / <c>mkisofs</c>). Turns loose game files — or a folder a
/// user extracted from a rip the emulator can't read (e.g. UDF) — into a disc that dosbox and a
/// guest OS can mount. The Windows build used IMAPI2 COM; on Linux these tools fill the same role.
/// </summary>
public static class IsoBuilder
{
    /// <summary>The mkisofs-compatible tools we try, in order. xorriso speaks mkisofs via "-as mkisofs".</summary>
    private static readonly (string Exe, bool MkisofsEmulation)[] Tools =
    [
        ("xorriso", true),
        ("genisoimage", false),
        ("mkisofs", false),
    ];

    /// <summary>True if a usable ISO-building tool is on PATH (gate the "Add disc from folder" action).</summary>
    public static bool IsAvailable => ResolveTool() is not null;

    /// <summary>Build <paramref name="isoPath"/> from the contents of <paramref name="folderPath"/>
    /// (the folder's files land at the disc root). Synchronous — run it on a background thread, not
    /// the UI thread.</summary>
    public static void BuildFromFolder(string folderPath, string isoPath, string volumeLabel)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        var tool = ResolveTool()
            ?? throw new NotSupportedException(
                "No ISO-building tool found. Install 'xorriso' (or genisoimage/mkisofs) to add discs from a folder.");

        var psi = new ProcessStartInfo
        {
            FileName = tool.Path,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // xorriso needs the "-as mkisofs" prefix to accept mkisofs-style arguments.
        if (tool.MkisofsEmulation)
        {
            psi.ArgumentList.Add("-as");
            psi.ArgumentList.Add("mkisofs");
        }
        // ISO9660 level 3 (long names / large files) + Joliet — what dosbox and DOS guests expect.
        psi.ArgumentList.Add("-iso-level");
        psi.ArgumentList.Add("3");
        psi.ArgumentList.Add("-J");
        psi.ArgumentList.Add("-joliet-long");
        psi.ArgumentList.Add("-V");
        psi.ArgumentList.Add(SanitizeLabel(volumeLabel));
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(isoPath);
        psi.ArgumentList.Add(folderPath); // the folder's CONTENTS become the disc root

        using var proc = Process.Start(psi)
            ?? throw new NotSupportedException($"Couldn't start {tool.Path}.");
        string stderr = proc.StandardError.ReadToEnd();
        proc.StandardOutput.ReadToEnd(); // drain so the pipe can't deadlock
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            try { File.Delete(isoPath); } catch { /* leave nothing half-written */ }
            throw new InvalidOperationException(
                $"ISO build failed (exit {proc.ExitCode}): {Tail(stderr)}");
        }
    }

    /// <summary>The first available tool on PATH, or null if none is installed.</summary>
    private static (string Path, bool MkisofsEmulation)? ResolveTool()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var (exe, emulation) in Tools)
            foreach (var dir in dirs)
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate))
                    return (candidate, emulation);
            }
        return null;
    }

    // ISO9660 volume identifier: up to 32 chars, A-Z / 0-9 / underscore.
    private static string SanitizeLabel(string label)
    {
        var cleaned = new string(label.ToUpperInvariant()
            .Select(c => (char.IsLetterOrDigit(c) && c < 128) || c == '_' ? c : '_')
            .ToArray()).Trim('_');
        if (cleaned.Length > 32)
            cleaned = cleaned[..32];
        return cleaned.Length == 0 ? "DISC" : cleaned;
    }

    private static string Tail(string text)
    {
        text = text.Trim();
        return text.Length <= 400 ? text : "…" + text[^400..];
    }
}
