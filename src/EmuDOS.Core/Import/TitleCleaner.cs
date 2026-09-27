using System.Text.RegularExpressions;

namespace EmuDOS.Core.Import;

/// <summary>
/// Turns a dropped folder or archive name into a display title, the way library frontends present
/// dump-style names: underscores become spaces, bracketed tags go — "(1990)", "(USA)", "[!]",
/// "(Disc 1)" — and a trailing article comes back to the front ("Title, The" → "The Title").
/// Archive-style names joined by underscores also lose a trailing year or platform/language tag.
/// </summary>
public static partial class TitleCleaner
{
    private static readonly HashSet<string> TrailingTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "dos", "msdos", "ms-dos", "pc", "en", "eng", "us", "usa", "eu", "cd", "cdrom", "cd-rom",
    };

    /// <summary>The cleaned title for <paramref name="name"/> (a folder or file name without its
    /// extension); the name itself when cleaning would leave nothing.</summary>
    public static string Clean(string name)
    {
        var original = (name ?? string.Empty).Trim();
        if (original.Length == 0)
            return "Untitled";

        var underscored = original.Contains('_');
        var s = original.Replace('_', ' ');

        // Words-Joined-By-Hyphens (two or more hyphens in one run) read as words; "X-Fighter" stays.
        s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Count(c => c == '-') >= 2 && t.Any(char.IsLetter) ? t.Replace('-', ' ') : t));

        var withoutTags = Brackets().Replace(s, " ");
        if (!string.IsNullOrWhiteSpace(withoutTags))
            s = withoutTags;
        s = Spaces().Replace(s, " ").Trim();

        if (underscored)
        {
            var words = s.Split(' ').ToList();
            while (words.Count > 1 && (TrailingTags.Contains(words[^1]) || IsYear(words[^1])))
                words.RemoveAt(words.Count - 1);
            s = string.Join(' ', words);
        }

        if (TrailingArticle().Match(s) is { Success: true } m)
            s = $"{m.Groups["article"].Value} {m.Groups["title"].Value}";

        s = s.Trim().TrimEnd('-', ',').Trim();
        return s.Length == 0 ? original : s;
    }

    private static bool IsYear(string word) =>
        word.Length == 4 && int.TryParse(word, out var year) && year is >= 1980 and <= 1999;

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(?<title>.+?),\s*(?<article>The|A|An)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingArticle();
}
