using System.Text.RegularExpressions;

namespace Iseberg.Core;

public sealed record ReplacementOptions(bool MatchCase = false, bool WholeWord = false, bool RegularExpression = false,
    bool SearchUp = false, bool WrapAround = true);
public sealed record TextReplacement(int Start, int Length, string Text);

public static class ReplacementSearch
{
    public static IReadOnlyList<TextReplacement> Find(string text, string pattern, string replacement,
        ReplacementOptions options, int start = 0, int? length = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        var count = length ?? text.Length - start;
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + count, text.Length);
        var expression = options.RegularExpression ? pattern : Regex.Escape(pattern);
        if (options.WholeWord) expression = @"(?<!\w)(?:" + expression + @")(?!\w)";
        var flags = RegexOptions.CultureInvariant | RegexOptions.Multiline;
        if (!options.MatchCase) flags |= RegexOptions.IgnoreCase;
        var regex = new Regex(expression, flags, TimeSpan.FromMilliseconds(500));
        return regex.Matches(text).Cast<Match>().Where(m => m.Index >= start && m.Index + m.Length <= start + count)
            .Select(m => new TextReplacement(m.Index, m.Length, options.RegularExpression ? m.Result(replacement) : replacement)).ToArray();
    }

    public static TextReplacement? Next(IReadOnlyList<TextReplacement> matches, int offset, ReplacementOptions options)
    {
        if (matches.Count == 0) return null;
        if (options.SearchUp)
            return matches.LastOrDefault(m => m.Start < offset) ?? (options.WrapAround ? matches[^1] : null);
        return matches.FirstOrDefault(m => m.Start >= offset) ?? (options.WrapAround ? matches[0] : null);
    }
}
