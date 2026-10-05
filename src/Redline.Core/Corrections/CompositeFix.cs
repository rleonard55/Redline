using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Text;

namespace Redline.Core.Corrections;

public enum FixScope { Sentence, Paragraph, Selection }

/// <summary>
/// One change in a combined fix: replace <see cref="Range"/> (which reads <see cref="Original"/> in the
/// snapshot) with <see cref="Replacement"/>. Deletions already include the adjacent space they take.
/// </summary>
public sealed record FixEdit(TextRange Range, string Original, string Replacement, IssueCategory Category, string Source, bool DefaultOn = true);

/// <summary>The grammar model's version of one whole sentence, as edits of the snapshot.</summary>
public sealed record SentenceAlternative(TextRange Sentence, IReadOnlyList<FixEdit> Edits);

public enum PreviewKind { Same, Removed, Inserted }

public readonly record struct PreviewSegment(string Text, PreviewKind Kind);

/// <summary>
/// Every fixable issue inside a sentence, paragraph or selection, as non-overlapping edits that can be
/// applied in one pass (back to front, so earlier offsets never shift). Pure: no UI, no UIA.
/// </summary>
public sealed class CompositeFix
{
    public required FixScope Scope { get; init; }
    public required TextRange Range { get; init; }
    public required string Text { get; init; }
    public required long SnapshotVersion { get; init; }
    public required IReadOnlyList<FixEdit> Edits { get; init; }

    /// <summary>
    /// Combines <paramref name="issues"/> (already filtered for the user) that lie entirely inside
    /// <paramref name="range"/>. Each issue contributes its first suggestion; where issues overlap,
    /// primary analyzers win over supplementary ones, then the earlier and shorter issue.
    /// </summary>
    public static CompositeFix Build(string text, long version, IReadOnlyList<TextIssue> issues, TextRange range, FixScope scope)
    {
        var candidates = issues
            .Where(i => i.Suggestions.Count > 0 && i.Length > 0 && i.StartOffset >= range.Start && i.Range.End <= range.End
                        && i.Range.End <= text.Length && text.Substring(i.StartOffset, i.Length) == i.OriginalText)
            .OrderBy(i => i.Supplementary)
            .ThenBy(i => i.StartOffset)
            .ThenBy(i => i.Length)
            .Select(i => ToEdit(text, i));

        return new CompositeFix { Scope = scope, Range = range, Text = text, SnapshotVersion = version, Edits = Resolve(candidates) };
    }

    /// <summary>The trimmed sentence holding <paramref name="offset"/>, or its paragraph if it's between sentences.</summary>
    public static TextRange SentenceAt(string text, int offset)
    {
        foreach (var sentence in SentenceSplitter.Split(text))
        {
            if (sentence.Start <= offset && offset < sentence.End)
                return sentence;
        }
        return ParagraphAt(text, offset);
    }

    /// <summary>The paragraph (line) holding <paramref name="offset"/>, without surrounding blanks.</summary>
    public static TextRange ParagraphAt(string text, int offset) => Trim(text, ContextExpander.ToParagraph(text, new TextRange(offset, 0)));

    /// <summary>A selection without leading/trailing whitespace, clamped to the text.</summary>
    public static TextRange Trim(string text, TextRange range)
    {
        int start = Math.Clamp(range.Start, 0, text.Length), end = Math.Clamp(range.End, start, text.Length);
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return new TextRange(start, end - start);
    }

    /// <summary>
    /// The edits to apply: the ticked edits, except that a ticked alternative replaces every edit inside its
    /// sentence. Sorted by position; overlaps (which a well-formed alternative never causes) keep the first.
    /// </summary>
    public static IReadOnlyList<FixEdit> Combine(IEnumerable<FixEdit> tickedEdits, IEnumerable<SentenceAlternative> tickedAlternatives)
    {
        var alternatives = tickedAlternatives.ToList();
        var edits = tickedEdits.Where(e => !alternatives.Any(a => a.Sentence.IntersectsWith(e.Range)))
            .Concat(alternatives.SelectMany(a => a.Edits));
        return Resolve(edits.OrderBy(e => e.Range.Start));
    }

    /// <summary>The model's answer for <paramref name="sentence"/> (offsets relative to it) as document edits.</summary>
    public static SentenceAlternative ToAlternative(string text, TextRange sentence, IReadOnlyList<RewriteEdit> edits) =>
        new(sentence, edits.Select(e =>
        {
            var range = new TextRange(sentence.Start + e.Start, e.Length);
            if (e.Replacement.Length == 0) range = CorrectionMath.ExpandDeletion(text, range);
            return new FixEdit(range, text.Substring(range.Start, range.Length), e.Replacement, IssueCategory.Grammar, AnalyzerNames.GrammarModel);
        }).ToList());

    /// <summary>
    /// Worth offering: the alternative changes something, and reads differently from what the ticked
    /// <paramref name="edits"/> already make of that sentence.
    /// </summary>
    public static bool AddsSomething(string text, SentenceAlternative alternative, IEnumerable<FixEdit> edits)
    {
        if (alternative.Edits.Count == 0) return false;
        var inSentence = edits.Where(e => alternative.Sentence.IntersectsWith(e.Range)).ToList();
        return Apply(text, alternative.Edits) != Apply(text, inSentence);
    }

    /// <summary>The text with <paramref name="edits"/> applied (non-overlapping, any order).</summary>
    public static string Apply(string text, IEnumerable<FixEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(e => e.Range.Start))
            text = CorrectionMath.Apply(text, edit.Range, edit.Replacement);
        return text;
    }

    /// <summary>Runs of unchanged, removed and inserted text across <paramref name="range"/>, for the preview.</summary>
    public static IReadOnlyList<PreviewSegment> Preview(string text, TextRange range, IEnumerable<FixEdit> edits)
    {
        var segments = new List<PreviewSegment>();
        int pos = range.Start;
        foreach (var edit in edits.Where(e => e.Range.Start >= range.Start && e.Range.End <= range.End).OrderBy(e => e.Range.Start))
        {
            Add(segments, text[pos..edit.Range.Start], PreviewKind.Same);
            // Show only the part that changes ("an" -> "a" reads as "a[n]", not "[an][a]") when one side contains the other.
            var (prefix, removed, inserted, suffix) = Split(edit.Original, edit.Replacement);
            Add(segments, prefix, PreviewKind.Same);
            Add(segments, removed, PreviewKind.Removed);
            Add(segments, inserted, PreviewKind.Inserted);
            Add(segments, suffix, PreviewKind.Same);
            pos = edit.Range.End;
        }
        Add(segments, text[pos..range.End], PreviewKind.Same);
        return segments;
    }

    /// <summary>Keeps the edits that don't overlap one already kept, in the given order; returns them by position.</summary>
    private static List<FixEdit> Resolve(IEnumerable<FixEdit> edits)
    {
        var kept = new List<FixEdit>();
        foreach (var edit in edits)
        {
            if (kept.Any(k => k.Range.IntersectsWith(edit.Range) || (edit.Range.Length == 0 && k.Range.Contains(edit.Range.Start))))
                continue;
            kept.Add(edit);
        }
        kept.Sort((a, b) => a.Range.Start.CompareTo(b.Range.Start));
        return kept;
    }

    private static FixEdit ToEdit(string text, TextIssue issue)
    {
        var replacement = issue.Suggestions[0];
        var range = replacement.Length == 0 ? CorrectionMath.ExpandDeletion(text, issue.Range) : issue.Range;
        // A capitalized word inside a sentence is probably a name the speller doesn't know: offer, don't tick.
        bool defaultOn = !(issue.Category == IssueCategory.Spelling && char.IsUpper(issue.OriginalText[0]) && !StartsSentence(text, issue.StartOffset));
        return new FixEdit(range, text.Substring(range.Start, range.Length), replacement, issue.Category, issue.Analyzer, defaultOn);
    }

    private static bool StartsSentence(string text, int offset)
    {
        int i = offset - 1;
        while (i >= 0 && (text[i] == ' ' || text[i] == (char)0x09 || text[i] is '"' or '(' or (char)0x201C)) i--;
        return i < 0 || text[i] is '.' or '!' or '?' or (char)0x0A or (char)0x0D or (char)0x0B or (char)0x2029;
    }

    /// <summary>
    /// Shared text at either end stays plain, but only up to a word boundary: "today" -> "today," shows
    /// an inserted comma, while "go" -> "goes" shows the whole word replaced rather than "go[es]".
    /// </summary>
    private static (string Prefix, string Removed, string Inserted, string Suffix) Split(string original, string replacement)
    {
        int p = 0;
        while (p < original.Length && p < replacement.Length && original[p] == replacement[p]) p++;
        while (p > 0 && (InsideWord(original, p) || InsideWord(replacement, p))) p--;

        int max = Math.Min(original.Length, replacement.Length) - p, s = 0;
        while (s < max && original[^(s + 1)] == replacement[^(s + 1)]) s++;
        while (s > 0 && (InsideWord(original, original.Length - s) || InsideWord(replacement, replacement.Length - s))) s--;

        return (original[..p], original[p..^s], replacement[p..^s], original[^s..]);
    }

    /// <summary>True when position <paramref name="i"/> falls between two word characters.</summary>
    private static bool InsideWord(string s, int i) => i > 0 && i < s.Length && char.IsLetterOrDigit(s[i - 1]) && char.IsLetterOrDigit(s[i]);

    private static void Add(List<PreviewSegment> segments, string text, PreviewKind kind)
    {
        if (text.Length == 0) return;
        if (segments.Count > 0 && segments[^1].Kind == kind)
            segments[^1] = new PreviewSegment(segments[^1].Text + text, kind);
        else
            segments.Add(new PreviewSegment(text, kind));
    }
}
