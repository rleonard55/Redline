using Redline.Core.Models;

namespace Redline.Core.Corrections;

public static class CorrectionMath
{
    /// <summary>The document text after replacing <paramref name="range"/> with <paramref name="replacement"/>.</summary>
    public static string Apply(string text, TextRange range, string replacement) =>
        string.Concat(text.AsSpan(0, range.Start), replacement, text.AsSpan(range.End));

    /// <summary>
    /// For deletions, widens the range to take one adjacent space with it so "the the cat" becomes
    /// "the cat" rather than "the  cat". Prefers the space before the word; takes the one after only
    /// when the word starts a line.
    /// </summary>
    public static TextRange ExpandDeletion(string text, TextRange range)
    {
        bool spaceBefore = range.Start > 0 && text[range.Start - 1] == ' ';
        bool spaceAfter = range.End < text.Length && text[range.End] == ' ';
        bool atLineStart = range.Start == 0 || text[range.Start - 1] is '\n' or '\r';

        if (spaceBefore && (range.End == text.Length || spaceAfter || char.IsPunctuation(text[range.End]) || text[range.End] is '\n' or '\r'))
            return new TextRange(range.Start - 1, range.Length + 1);
        if (atLineStart && spaceAfter)
            return new TextRange(range.Start, range.Length + 1);
        return range;
    }

    /// <summary>
    /// UIA "character" units are provider-defined: most providers count UTF-16 code units, but some
    /// count a surrogate pair or a CRLF as one character. Returns the distinct (start, length) pairs,
    /// in provider units, that <paramref name="range"/> (UTF-16 offsets into <paramref name="text"/>)
    /// maps to under each convention, most likely first. Callers select each candidate and keep the
    /// one whose text matches.
    /// </summary>
    public static IReadOnlyList<TextRange> ProviderUnitCandidates(string text, TextRange range)
    {
        var candidates = new List<TextRange>(4);
        foreach (var (pairsAsOne, crlfAsOne) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            int start = Count(text, 0, range.Start, pairsAsOne, crlfAsOne);
            int length = Count(text, range.Start, range.End, pairsAsOne, crlfAsOne);
            var candidate = new TextRange(start, length);
            if (!candidates.Contains(candidate))
                candidates.Add(candidate);
        }
        return candidates;
    }

    /// <summary>
    /// How far (in provider units) a provider's offsets could plausibly have drifted from UTF-16 by
    /// <paramref name="offset"/>: a few units per embedded object (U+FFFC) and one per line break
    /// before it. Zero when there are neither, i.e. when the fixed conventions are the whole story.
    /// </summary>
    public static int MaxPlausibleDrift(string text, int offset)
    {
        int objects = 0, breaks = 0;
        for (int i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == ObjectReplacement) objects++;
            else if (IsLineBreak(text[i])) breaks++;
        }
        return objects == 0 && breaks == 0 ? 0 : Math.Min(64, 3 * objects + breaks + 2);
    }

    /// <summary>Drift values to try: the hint first, then spiraling outward, within the given maximum.</summary>
    public static IEnumerable<int> DriftOrder(int hint, int max)
    {
        hint = Math.Clamp(hint, -max, max);
        yield return hint;
        for (int step = 1; step <= 2 * max; step++)
        {
            int up = hint + step, down = hint - step;
            if (up <= max) yield return up;
            if (down >= -max) yield return down;
            if (up > max && down < -max) yield break;
        }
    }

    /// <summary>
    /// Up to <paramref name="maxContext"/> characters immediately before and after <paramref name="range"/>,
    /// stopping at line breaks and embedded objects (whose unit length is provider-specific).
    /// </summary>
    public static (string Before, string After) ContextAround(string text, TextRange range, int maxContext = 6)
    {
        int b = range.Start;
        while (b > 0 && range.Start - b < maxContext && !IsBoundary(text[b - 1])) b--;
        int a = range.End;
        while (a < text.Length && a - range.End < maxContext && !IsBoundary(text[a])) a++;
        return (text[b..range.Start], text[range.End..a]);
    }

    /// <summary>
    /// True if <paramref name="actual"/> equals <paramref name="expected"/> apart from whitespace and
    /// line breaks at the very end of the document. Rich editors represent an emptied last paragraph
    /// differently (Chromium: a trailing space instead of a line break); every other character must
    /// still match exactly.
    /// </summary>
    public static bool EquivalentForVerification(string actual, string expected) =>
        actual == expected || TrimTrailingBlank(actual) == TrimTrailingBlank(expected);

    private static string TrimTrailingBlank(string s)
    {
        int end = s.Length;
        while (end > 0 && (char.IsWhiteSpace(s[end - 1]) || IsLineBreak(s[end - 1]))) end--;
        return s[..end];
    }

    /// <summary>U+FFFC, which UIA text uses for embedded objects such as images.</summary>
    private const char ObjectReplacement = (char)0xFFFC;

    private static bool IsLineBreak(char c) => c is (char)0x0A or (char)0x0D or (char)0x0B or (char)0x2029;

    private static bool IsBoundary(char c) => c == ObjectReplacement || IsLineBreak(c);

    private static int Count(string text, int from, int to, bool pairsAsOne, bool crlfAsOne)
    {
        int units = to - from;
        for (int i = from; i < to - 1; i++)
        {
            if (pairsAsOne && char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1])) units--;
            else if (crlfAsOne && text[i] == '\r' && text[i + 1] == '\n') units--;
        }
        return units;
    }
}
