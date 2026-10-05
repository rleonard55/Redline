using Redline.Core.Corrections;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Text;
using Xunit;

namespace Redline.Core.Tests;

public class CompositeFixTests
{
    private static TextIssue Issue(string text, string word, string suggestion, IssueCategory category = IssueCategory.Spelling,
        bool supplementary = false, int occurrence = 0, string analyzer = "Spell")
    {
        int start = -1;
        for (int i = 0; i <= occurrence; i++)
            start = text.IndexOf(word, start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0, word);
        return new TextIssue
        {
            StartOffset = start, Length = word.Length, OriginalText = word, Category = category, Message = "m",
            Suggestions = [suggestion], Supplementary = supplementary, Analyzer = analyzer, SnapshotVersion = 3,
        };
    }

    private static TextRange All(string text) => new(0, text.Length);

    [Fact]
    public void Build_TakesFirstSuggestions_AndApplyingThemFixesTheText()
    {
        const string text = "This is an tset. She go home.";
        var fix = CompositeFix.Build(text, 3, [Issue(text, "an", "a", IssueCategory.Grammar), Issue(text, "tset", "test"), Issue(text, "go", "goes", IssueCategory.Grammar)],
            All(text), FixScope.Paragraph);

        Assert.Equal(3, fix.Edits.Count);
        Assert.Equal(3, fix.SnapshotVersion);
        Assert.Equal("This is a test. She goes home.", CompositeFix.Apply(text, fix.Edits));
    }

    [Fact]
    public void Build_KeepsOnlyIssuesInsideTheScope()
    {
        const string text = "One tset. Two tset.";
        var issues = new[] { Issue(text, "tset", "test"), Issue(text, "tset", "test", occurrence: 1) };

        var fix = CompositeFix.Build(text, 3, issues, CompositeFix.SentenceAt(text, 14), FixScope.Sentence);

        var edit = Assert.Single(fix.Edits);
        Assert.Equal(14, edit.Range.Start);
    }

    [Fact]
    public void Build_Overlaps_PrimaryWinsOverSupplementary_ThenEarlierAndShorter()
    {
        const string text = "I has went there.";
        var model = Issue(text, "has went", "had gone", IssueCategory.Grammar, supplementary: true, analyzer: AnalyzerNames.GrammarModel);
        var harper = Issue(text, "went", "gone", IssueCategory.Grammar, analyzer: "Harper");

        var fix = CompositeFix.Build(text, 3, [model, harper], All(text), FixScope.Paragraph);

        var edit = Assert.Single(fix.Edits);
        Assert.Equal("Harper", edit.Source);
        Assert.Equal("I has gone there.", CompositeFix.Apply(text, fix.Edits));
    }

    [Fact]
    public void Build_SkipsIssuesWithoutSuggestions_OrThatNoLongerMatch()
    {
        const string text = "A tset here.";
        var noSuggestion = Issue(text, "tset", "x") with { Suggestions = [] };
        var stale = Issue(text, "tset", "test") with { OriginalText = "test" };

        Assert.Empty(CompositeFix.Build(text, 3, [noSuggestion, stale], All(text), FixScope.Paragraph).Edits);
    }

    [Fact]
    public void Build_Deletion_TakesOneSpaceWithIt()
    {
        const string text = "I saw the the cat.";
        var fix = CompositeFix.Build(text, 3, [Issue(text, "the", "", occurrence: 1)], All(text), FixScope.Sentence);

        Assert.Equal(new TextRange(9, 4), fix.Edits[0].Range);
        Assert.Equal("I saw the cat.", CompositeFix.Apply(text, fix.Edits));
    }

    [Fact]
    public void Build_CapitalizedWordInsideASentence_IsOfferedButNotTicked()
    {
        const string text = "Tset with Grnt. Wrod here.";
        var fix = CompositeFix.Build(text, 3, [Issue(text, "Tset", "Test"), Issue(text, "Grnt", "Grant"), Issue(text, "Wrod", "Word")],
            All(text), FixScope.Paragraph);

        Assert.Equal([true, false, true], fix.Edits.Select(e => e.DefaultOn));
    }

    [Fact]
    public void SentenceAndParagraphAt_FindTheEnclosingRanges()
    {
        const string text = "First one. Second one here.\n  Next para.  ";
        Assert.Equal("Second one here.", Sub(text, CompositeFix.SentenceAt(text, 15)));
        Assert.Equal("First one. Second one here.", Sub(text, CompositeFix.ParagraphAt(text, 15)));
        Assert.Equal("Next para.", Sub(text, CompositeFix.ParagraphAt(text, 32)));
        Assert.Equal("Next para.", Sub(text, CompositeFix.Trim(text, new TextRange(28, 14))));
    }

    [Fact]
    public void Combine_ATickedAlternativeReplacesTheEditsInItsSentence()
    {
        const string text = "Me and him goes. A tset.";
        var spelling = new FixEdit(new TextRange(19, 4), "tset", "test", IssueCategory.Spelling, "Spell");
        var harper = new FixEdit(new TextRange(11, 4), "goes", "go", IssueCategory.Grammar, "Harper");
        var sentence = new TextRange(0, 16);
        var alternative = CompositeFix.ToAlternative(text, sentence,
            RewriteDiff.ComputeLoose("Me and him goes.", "He and I go."));

        var edits = CompositeFix.Combine([spelling, harper], [alternative]);

        Assert.Equal("He and I go. A test.", CompositeFix.Apply(text, edits));
        Assert.DoesNotContain(harper, edits);
        Assert.True(CompositeFix.AddsSomething(text, alternative, [harper]));
    }

    [Fact]
    public void AddsSomething_IsFalse_WhenTheModelAgreesWithTheFixes()
    {
        const string text = "She go home.";
        var harper = new FixEdit(new TextRange(4, 2), "go", "goes", IssueCategory.Grammar, "Harper");
        var alternative = CompositeFix.ToAlternative(text, new TextRange(0, text.Length), RewriteDiff.ComputeLoose(text, "She goes home."));

        Assert.False(CompositeFix.AddsSomething(text, alternative, [harper]));
        Assert.True(CompositeFix.AddsSomething(text, alternative, []));
    }

    [Fact]
    public void Preview_ShowsRemovedAndInsertedRuns_SplitAtWordBoundaries()
    {
        const string text = "Well she go home today";
        var edits = new[]
        {
            new FixEdit(new TextRange(9, 2), "go", "goes", IssueCategory.Grammar, "Harper"),
            new FixEdit(new TextRange(17, 5), "today", "today.", IssueCategory.Punctuation, "GRMR"),
        };

        var preview = CompositeFix.Preview(text, new TextRange(5, 17), edits);

        Assert.Equal(
        [
            new PreviewSegment("she ", PreviewKind.Same),
            new PreviewSegment("go", PreviewKind.Removed),
            new PreviewSegment("goes", PreviewKind.Inserted),
            new PreviewSegment(" home today", PreviewKind.Same),
            new PreviewSegment(".", PreviewKind.Inserted),
        ], preview);
    }

    [Fact]
    public void ComputeLoose_KeepsAnswersTheStrictDiffRejects()
    {
        const string original = "i has went too the stor yesterday an buyed sum food";
        const string corrected = "I had gone to the store yesterday and bought some food.";

        Assert.Empty(RewriteDiff.Compute(original, corrected));
        var edits = RewriteDiff.ComputeLoose(original, corrected);
        Assert.NotEmpty(edits);
        Assert.Empty(RewriteDiff.ComputeLoose("Short note here.", "This is a considerably longer sentence that the model invented for no reason at all."));
    }

    [Fact]
    public void ComputeLoose_RejectsAnswersThatDropWords()
    {
        // Seen live: the model deleted the misspelled word and its neighbours.
        Assert.Empty(RewriteDiff.ComputeLoose("This is an tset of the new feature.", "This is a new feature."));
        Assert.NotEmpty(RewriteDiff.ComputeLoose("I saw the the cat.", "I saw the cat."));
        Assert.NotEmpty(RewriteDiff.ComputeLoose("me and him has went to the store.", "He and I went to the store."));
    }

    private static string Sub(string text, TextRange range) => text.Substring(range.Start, range.Length);
}
