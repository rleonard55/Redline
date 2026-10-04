using Redline.Core.Corrections;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Pipeline;
using Redline.Core.Settings;
using Redline.Core.Text;
using Xunit;

namespace Redline.Core.Tests;

public class SentenceSplitterTests
{
    private static List<string> Split(string text) => SentenceSplitter.Split(text).Select(r => text.Substring(r.Start, r.Length)).ToList();

    [Fact]
    public void SplitsAfterTerminalPunctuationFollowedBySpace()
    {
        Assert.Equal(["This is an tset.", "She go to school!", "Really?"], Split("This is an tset. She go to school!  Really?"));
    }

    [Fact]
    public void SplitsAtLineBreaks_AndKeepsUnterminatedText()
    {
        Assert.Equal(["Hi team", "i dont know weather to come"], Split("Hi team\r\n\r\ni dont know weather to come"));
    }

    [Fact]
    public void DoesNotSplitInsideNumbersOrVersions_AndKeepsClosingQuotes()
    {
        Assert.Equal(["We shipped 0.6.0 today.", "He said \"stop.\"", "Then left."],
            Split("We shipped 0.6.0 today. He said \"stop.\" Then left."));
    }

    [Fact]
    public void DropsPiecesWithoutLetters()
    {
        Assert.Equal(["Total:"], Split("Total:\n42.\n---\n"));
    }

    [Fact]
    public void RangesPointIntoTheOriginalText()
    {
        const string text = "  One two.  Three four. ";
        var ranges = SentenceSplitter.Split(text);
        Assert.Equal([new TextRange(2, 8), new TextRange(12, 11)], ranges);
    }
}

public class RewriteDiffTests
{
    private static string Apply(string original, IReadOnlyList<RewriteEdit> edits)
    {
        var result = original;
        foreach (var e in edits.OrderByDescending(e => e.Start))
            result = result[..e.Start] + e.Replacement + result[e.End..];
        return result;
    }

    private static List<(string Original, string Replacement)> Describe(string original, IReadOnlyList<RewriteEdit> edits) =>
        edits.Select(e => (original.Substring(e.Start, e.Length), e.Replacement)).ToList();

    [Fact]
    public void IdenticalOrEmptyOutput_HasNoEdits()
    {
        Assert.Empty(RewriteDiff.Compute("All good here.", "All good here."));
        Assert.Empty(RewriteDiff.Compute("All good here.", "  All good here.\n"));
        Assert.Empty(RewriteDiff.Compute("All good here.", ""));
        Assert.Empty(RewriteDiff.Compute("All good here.", null));
    }

    [Fact]
    public void SingleWordReplacements_AreSeparateEdits()
    {
        const string original = "The results was better then expected.";
        var edits = RewriteDiff.Compute(original, "The results were better than expected.");
        Assert.Equal([("was", "were"), ("then", "than")], Describe(original, edits));
        Assert.Equal("The results were better than expected.", Apply(original, edits));
    }

    [Fact]
    public void ArticleFix_LeavesTheMisspelledWordAlone()
    {
        const string original = "This is an tset.";
        var edits = RewriteDiff.Compute(original, "This is a tset.");
        Assert.Equal([("an", "a")], Describe(original, edits));
    }

    [Fact]
    public void Contractions_AndCapitalization()
    {
        const string original = "i dont know weather to bring a umbrella today";
        var corrected = "I don't know whether to bring an umbrella today.";
        var edits = RewriteDiff.Compute(original, corrected);
        Assert.Equal([("i", "I"), ("dont", "don't"), ("weather", "whether"), ("a", "an")], Describe(original, edits));
        // The added final period is dropped: noise in chats and titles.
        Assert.Equal("I don't know whether to bring an umbrella today", Apply(original, edits));
    }

    [Fact]
    public void DeletedWord_TakesItsSpaceWithIt()
    {
        const string original = "I saw the the cat.";
        var edits = RewriteDiff.Compute(original, "I saw the cat.");
        Assert.Single(edits);
        Assert.Equal("I saw the cat.", Apply(original, edits));
    }

    [Fact]
    public void InsertedPunctuation_AttachesToThePreviousWord()
    {
        const string original = "However we left early.";
        var edits = RewriteDiff.Compute(original, "However, we left early.");
        Assert.Equal([("However", "However,")], Describe(original, edits));
    }

    [Fact]
    public void InsertedWord_AttachesToThePreviousWordWithItsSpace()
    {
        const string original = "I want go home.";
        var edits = RewriteDiff.Compute(original, "I want to go home.");
        var edit = Assert.Single(edits);
        Assert.Equal("I want to go home.", Apply(original, edits));
        Assert.StartsWith("want", original.Substring(edit.Start, edit.Length));
    }

    [Fact]
    public void InsertionAtTheStart_AttachesToTheFirstWord()
    {
        const string original = "went to the store.";
        var edits = RewriteDiff.Compute(original, "I went to the store.");
        Assert.Equal("I went to the store.", Apply(original, edits));
        Assert.Equal(0, Assert.Single(edits).Start);
    }

    [Fact]
    public void Rewrites_AreRejected()
    {
        Assert.Empty(RewriteDiff.Compute("The meeting is at noon.", "Lunch will be served at the conference later."));
        Assert.Empty(RewriteDiff.Compute("Short note here.", "This is a considerably longer sentence that the model invented."));
    }

    [Fact]
    public void WhitespaceOnlyChanges_AreIgnored()
    {
        Assert.Empty(RewriteDiff.Compute("Two  spaces here.", "Two spaces here."));
    }
}

public class GrammarModelPipelineTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(30);
    private static readonly TextSurfaceContext Surface = new() { SurfaceId = "s1", ProcessName = "test.exe" };
    private static long _version;

    private static TextSnapshot Snap(string text) => new(text, Interlocked.Increment(ref _version), DateTimeOffset.UtcNow);

    /// <summary>Flags every occurrence of <see cref="Word"/>.</summary>
    private sealed class WordAnalyzer(string name, string word, bool supplementary) : ITextAnalyzer
    {
        public int Runs;
        public string Word { get; set; } = word;
        public string Name => name;
        public bool IsAvailable => true;
        public bool IsSupplementary => supplementary;

        public Task<IReadOnlyList<TextIssue>> AnalyzeAsync(TextAnalysisRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            var issues = new List<TextIssue>();
            for (int i = request.Text.IndexOf(Word, StringComparison.Ordinal); i >= 0; i = request.Text.IndexOf(Word, i + 1, StringComparison.Ordinal))
                issues.Add(new TextIssue
                {
                    StartOffset = request.ContextOffset + i, Length = Word.Length, OriginalText = Word,
                    Category = IssueCategory.Grammar, Message = name, Analyzer = name,
                });
            return Task.FromResult<IReadOnlyList<TextIssue>>(issues);
        }
    }

    private static async Task<AnalysisResult> Next(AnalysisPipeline pipeline, Action action)
    {
        var tcs = new TaskCompletionSource<AnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<AnalysisResult> handler = (_, r) => tcs.TrySetResult(r);
        pipeline.AnalysisCompleted += handler;
        try
        {
            action();
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            pipeline.AnalysisCompleted -= handler;
        }
    }

    [Fact]
    public async Task SupplementaryIssues_AreTagged_AndKept()
    {
        var primary = new WordAnalyzer("Primary", "an tset", supplementary: false);
        var model = new WordAnalyzer(AnalyzerNames.GrammarModel, "an", supplementary: true);
        using var pipeline = new AnalysisPipeline([primary, model], new AnalysisPipelineOptions { Debounce = Debounce });

        var result = await Next(pipeline, () => pipeline.Submit(Surface, Snap("an apple or an tset")));

        // Overlaps are resolved after user filters (IssueCacheManager), so the pipeline keeps all of them.
        Assert.Equal([(0, AnalyzerNames.GrammarModel, true), (12, AnalyzerNames.GrammarModel, true), (12, "Primary", false)],
            result.Issues.Issues.Select(i => (i.StartOffset, i.Analyzer, i.Supplementary)));
    }

    [Fact]
    public async Task Refresh_ReanalyzesUnchangedText()
    {
        var model = new WordAnalyzer(AnalyzerNames.GrammarModel, "zzz", supplementary: true);
        using var pipeline = new AnalysisPipeline([model], new AnalysisPipelineOptions { Debounce = Debounce });

        var snapshot = Snap("She go to school.");
        var first = await Next(pipeline, () => pipeline.Submit(Surface, snapshot));
        Assert.Empty(first.Issues.Issues);

        // The background model now "knows" about "go"; resubmitting the same text would be skipped.
        model.Word = "go";
        var refreshed = await Next(pipeline, pipeline.Refresh);
        Assert.Equal(snapshot.Version, refreshed.Snapshot.Version);
        Assert.Equal(4, Assert.Single(refreshed.Issues.Issues).StartOffset);
    }

    [Fact]
    public async Task Refresh_AfterClear_DoesNothing()
    {
        var model = new WordAnalyzer(AnalyzerNames.GrammarModel, "go", supplementary: true);
        using var pipeline = new AnalysisPipeline([model], new AnalysisPipelineOptions { Debounce = Debounce });
        await Next(pipeline, () => pipeline.Submit(Surface, Snap("She go.")));
        pipeline.Clear();
        int runs = model.Runs;

        pipeline.Refresh();
        await Task.Delay(Debounce * 5);
        Assert.Equal(runs, model.Runs);
    }
}

public class GrammarModelFilterTests
{
    private sealed class NoDictionary : IPersonalDictionary
    {
        public event Action? Changed { add { } remove { } }
        public bool Contains(string word) => false;
        public void Add(string word) { }
        public bool Remove(string word) => false;
        public IReadOnlyCollection<string> Words => [];
    }

    [Fact]
    public void ModelIssues_AreHiddenWhileAiGrammarIsOff()
    {
        var cache = new IssueCacheManager(new NoDictionary(), new IgnoreList(null));
        var issue = new TextIssue
        {
            StartOffset = 4, Length = 2, OriginalText = "go", Category = IssueCategory.Grammar,
            Message = "m", Analyzer = AnalyzerNames.GrammarModel,
        };
        cache.Update("s1", IssueSet.From([issue], 1));

        Assert.Empty(cache.Get("s1")!.Issues);
        cache.SetWriting(new WritingSettings { AiGrammar = true });
        Assert.Single(cache.Get("s1")!.Issues);
    }

    private static TextIssue Issue(int start, string text, IssueCategory category, bool supplementary) => new()
    {
        StartOffset = start, Length = text.Length, OriginalText = text, Category = category, Message = "m",
        Analyzer = supplementary ? AnalyzerNames.GrammarModel : "Primary", Supplementary = supplementary,
    };

    // "an apple or an tset": the model flags "an" twice; spelling flags "tset", grammar flags "an tset".
    private static IssueSet Overlapping() => IssueSet.From(
    [
        Issue(0, "an", IssueCategory.Grammar, supplementary: true),
        Issue(12, "an", IssueCategory.Grammar, supplementary: true),
        Issue(15, "tset", IssueCategory.Spelling, supplementary: false),
        Issue(12, "an tset", IssueCategory.Grammar, supplementary: false),
    ], 1);

    [Fact]
    public void SupplementaryIssues_OverlappingVisiblePrimaryOnes_AreHidden()
    {
        var cache = new IssueCacheManager(new NoDictionary(), new IgnoreList(null));
        cache.SetWriting(new WritingSettings { AiGrammar = true });
        cache.Update("s1", Overlapping());

        Assert.Equal([(0, AnalyzerNames.GrammarModel), (12, "Primary"), (15, "Primary")],
            cache.Get("s1")!.Issues.Select(i => (i.StartOffset, i.Analyzer)));
    }

    [Fact]
    public void SupplementaryIssues_ShowWhenTheOverlappingPrimaryOnesAreHidden()
    {
        var ignores = new IgnoreList(null);
        var cache = new IssueCacheManager(new NoDictionary(), ignores);
        cache.SetWriting(new WritingSettings { AiGrammar = true, Spelling = false });
        var issues = Overlapping();
        cache.Update("s1", issues);
        Assert.Equal([0, 12], cache.Get("s1")!.Issues.Select(i => i.StartOffset));

        // With "tset" hidden by category and "an tset" ignored, nothing suppresses the model's "an" at 12.
        ignores.IgnoreInSession(issues.Issues.Single(i => i is { Analyzer: "Primary", Category: IssueCategory.Grammar }));
        Assert.Equal([(0, AnalyzerNames.GrammarModel), (12, AnalyzerNames.GrammarModel)],
            cache.Get("s1")!.Issues.Select(i => (i.StartOffset, i.Analyzer)));
    }

    [Fact]
    public void AiGrammar_IsOffByDefault()
    {
        Assert.False(new RedlineSettings().Validated().Writing.AiGrammar);
    }
}
