namespace Redline.Core.Text;

/// <summary>One edit to an original sentence: replace [Start, Start+Length) with <see cref="Replacement"/>.</summary>
public readonly record struct RewriteEdit(int Start, int Length, string Replacement)
{
    public int End => Start + Length;
}

/// <summary>
/// Turns a model's corrected sentence back into small, word-level edits of the original, and
/// rejects outputs that read as a rewrite rather than a correction.
/// </summary>
public static class RewriteDiff
{
    /// <summary>More separate edits than this in one sentence = the model rewrote it.</summary>
    public const int MaxEdits = 6;

    /// <summary>Changed word characters / all word characters above this = a rewrite.</summary>
    public const double MaxChangedFraction = 0.5;

    /// <summary>
    /// Word-level edits that turn <paramref name="original"/> into <paramref name="corrected"/>, or
    /// none when they are identical, the output is empty, or the change is too large to be a
    /// correction. Whitespace-only edits and a sentence-final period the original didn't have are
    /// dropped. Insertions are attached to the neighbouring word so every edit has a range to underline.
    /// </summary>
    public static IReadOnlyList<RewriteEdit> Compute(string original, string? corrected)
    {
        if (string.IsNullOrWhiteSpace(corrected) || string.IsNullOrWhiteSpace(original))
            return [];
        corrected = corrected.Trim();
        if (string.Equals(original, corrected, StringComparison.Ordinal))
            return [];
        double ratio = (double)corrected.Length / original.Length;
        if (ratio < 0.6 || ratio > 1.6)
            return [];

        var a = Tokenize(original);
        var b = Tokenize(corrected);
        var hunks = Hunks(a, b);

        var edits = new List<RewriteEdit>();
        int changedWordChars = 0;
        foreach (var (aStart, aEnd, bStart, bEnd) in hunks)
        {
            var removed = Join(a, aStart, aEnd);
            var inserted = Join(b, bStart, bEnd);
            if (removed.Trim().Length == 0 && inserted.Trim().Length == 0)
                continue; // whitespace only
            if (aStart == a.Count && removed.Length == 0 && IsTerminalPunctuation(inserted))
                continue; // "...today" -> "...today." is noise in chats and titles

            changedWordChars += removed.Count(char.IsLetterOrDigit);
            edits.Add(ToEdit(a, aStart, aEnd, inserted));
        }

        if (edits.Count == 0 || edits.Count > MaxEdits)
            return [];
        int wordChars = original.Count(char.IsLetterOrDigit);
        if (wordChars == 0 || (double)changedWordChars / wordChars > MaxChangedFraction)
            return [];
        return Merge(edits);
    }

    /// <summary>Converts a hunk to an edit with a non-empty original range.</summary>
    private static RewriteEdit ToEdit(List<Token> a, int aStart, int aEnd, string inserted)
    {
        if (aEnd > aStart)
        {
            int start = a[aStart].Start, end = a[aEnd - 1].End;
            return new RewriteEdit(start, end - start, inserted);
        }

        // Pure insertion before token aStart: attach to the previous word (with the whitespace between),
        // or to the next word when inserting at the very start.
        int prev = aStart - 1;
        while (prev >= 0 && a[prev].IsSpace) prev--;
        if (prev >= 0)
        {
            int start = a[prev].Start, end = aStart > 0 ? a[aStart - 1].End : start;
            return new RewriteEdit(start, end - start, Join(a, prev, aStart) + inserted);
        }

        int next = aStart;
        while (next < a.Count && a[next].IsSpace) next++;
        int last = Math.Min(next, a.Count - 1);
        int s = a[aStart].Start, e = a[last].End;
        return new RewriteEdit(s, e - s, inserted + Join(a, aStart, last + 1));
    }

    /// <summary>Attaching insertions can make neighbouring edits touch or overlap; fold those together.</summary>
    private static List<RewriteEdit> Merge(List<RewriteEdit> edits)
    {
        edits.Sort((x, y) => x.Start.CompareTo(y.Start));
        var merged = new List<RewriteEdit>(edits.Count);
        foreach (var edit in edits)
        {
            if (merged.Count > 0 && edit.Start < merged[^1].End)
            {
                // Overlap only happens when an insertion was attached to the word another edit replaces;
                // keep the simpler (first) edit rather than guess at a combined replacement.
                continue;
            }
            merged.Add(edit);
        }
        return merged;
    }

    private static bool IsTerminalPunctuation(string s) => s.Length > 0 && s.All(c => c is '.' or '!' or '?');

    private readonly record struct Token(int Start, string Text)
    {
        public int End => Start + Text.Length;
        public bool IsSpace => Text.Length > 0 && char.IsWhiteSpace(Text[0]);
    }

    /// <summary>Words (letters, digits, apostrophes inside a word), whitespace runs, and single other characters.</summary>
    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < s.Length)
        {
            int start = i;
            if (IsWordChar(s[i]))
            {
                while (i < s.Length && (IsWordChar(s[i]) || (IsApostrophe(s[i]) && i + 1 < s.Length && IsWordChar(s[i + 1]))))
                    i++;
            }
            else if (char.IsWhiteSpace(s[i]))
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }
            else
            {
                i++;
            }
            tokens.Add(new Token(start, s[start..i]));
        }
        return tokens;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c);
    private static bool IsApostrophe(char c) => c is (char)0x27 or (char)0x2019;

    private static string Join(List<Token> tokens, int from, int to) =>
        string.Concat(tokens.Skip(from).Take(to - from).Select(t => t.Text));

    /// <summary>Maximal runs of non-matching tokens between LCS matches: (aStart, aEnd, bStart, bEnd).</summary>
    private static List<(int, int, int, int)> Hunks(List<Token> a, List<Token> b)
    {
        int n = a.Count, m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i].Text == b[j].Text ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var hunks = new List<(int, int, int, int)>();
        int x = 0, y = 0, hx = -1, hy = -1;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[x].Text == b[y].Text)
            {
                if (hx >= 0) { hunks.Add((hx, x, hy, y)); hx = -1; }
                x++; y++;
                continue;
            }
            if (hx < 0) { hx = x; hy = y; }
            if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y])) y++;
            else x++;
        }
        if (hx >= 0) hunks.Add((hx, n, hy, m));
        return hunks;
    }
}
