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
