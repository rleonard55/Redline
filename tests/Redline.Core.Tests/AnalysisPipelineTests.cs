using System.Collections.Concurrent;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Pipeline;
using Xunit;

namespace Redline.Core.Tests;

public class AnalysisPipelineTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Flags every occurrence of "bad"; records each request it receives.</summary>
    private sealed class BadWordAnalyzer : ITextAnalyzer
    {
        public ConcurrentQueue<TextAnalysisRequest> Requests { get; } = new();
        public TimeSpan Delay { get; init; }
        public string Name => "BadWord";
        public bool IsAvailable { get; init; } = true;

        public async Task<IReadOnlyList<TextIssue>> AnalyzeAsync(TextAnalysisRequest request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);

            var issues = new List<TextIssue>();
            for (int i = request.Text.IndexOf("bad", StringComparison.Ordinal); i >= 0; i = request.Text.IndexOf("bad", i + 1, StringComparison.Ordinal))
            {
                issues.Add(new TextIssue
                {
                    StartOffset = request.ContextOffset + i,
                    Length = 3,
                    OriginalText = "bad",
                    Category = IssueCategory.Spelling,
                    Message = "bad word",
                    Analyzer = Name,
                });
            }
            return issues;
        }
    }

    private sealed class ThrowingAnalyzer : ITextAnalyzer
    {
        public string Name => "Throws";
        public bool IsAvailable => true;
        public Task<IReadOnlyList<TextIssue>> AnalyzeAsync(TextAnalysisRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private static readonly TextSurfaceContext Surface = new() { SurfaceId = "s1", ProcessName = "test.exe" };
    private static long _version;

    private static TextSnapshot Snap(string text) => new(text, Interlocked.Increment(ref _version), DateTimeOffset.UtcNow);

    private static async Task<AnalysisResult> NextResult(AnalysisPipeline pipeline, Action submit)
    {
        var tcs = new TaskCompletionSource<AnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<AnalysisResult> handler = (_, r) => tcs.TrySetResult(r);
        pipeline.AnalysisCompleted += handler;
        try
        {
            submit();
            return await tcs.Task.WaitAsync(Timeout);
        }
        finally
        {
            pipeline.AnalysisCompleted -= handler;
        }
    }

    [Fact]
    public async Task RapidSubmits_AreDebouncedIntoOneRun_ForTheLatestSnapshot()
    {
        var analyzer = new BadWordAnalyzer();
        using var pipeline = new AnalysisPipeline([analyzer], new AnalysisPipelineOptions { Debounce = Debounce });

        var final = Snap("this is bad");
        var result = await NextResult(pipeline, () =>
        {
            pipeline.Submit(Surface, Snap("t"));
            pipeline.Submit(Surface, Snap("this"));
            pipeline.Submit(Surface, Snap("this is"));
            pipeline.Submit(Surface, final);
        });

        Assert.Equal(final.Version, result.Snapshot.Version);
        Assert.Single(analyzer.Requests);
        Assert.Equal(8, Assert.Single(result.Issues.Issues).StartOffset);
    }

    [Fact]
    public async Task SubmitDuringAnalysis_CancelsStaleRunAndAnalyzesLatest()
    {
        var analyzer = new BadWordAnalyzer { Delay = TimeSpan.FromMilliseconds(300) };
        using var pipeline = new AnalysisPipeline([analyzer], new AnalysisPipelineOptions { Debounce = Debounce });

        var results = new ConcurrentQueue<AnalysisResult>();
        pipeline.AnalysisCompleted += (_, r) => results.Enqueue(r);

        pipeline.Submit(Surface, Snap("bad one"));
        await Task.Delay(Debounce + TimeSpan.FromMilliseconds(80)); // first run is now inside the analyzer
        var latest = Snap("bad two bad");
        var result = await NextResult(pipeline, () => pipeline.Submit(Surface, latest));

        Assert.Equal(latest.Version, result.Snapshot.Version);
        Assert.Equal(2, result.Issues.Issues.Count);
        Assert.DoesNotContain(results, r => r.Snapshot.Text == "bad one");
    }

    [Fact]
    public async Task LargeDocument_EditIsAnalyzedIncrementally_AndOtherIssuesShift()
    {
        var analyzer = new BadWordAnalyzer();
        var options = new AnalysisPipelineOptions { Debounce = Debounce, FullAnalysisMaxChars = 10 };
        using var pipeline = new AnalysisPipeline([analyzer], options);

        const string original = "bad line one\nsecond line\nthird bad";
        var first = await NextResult(pipeline, () => pipeline.Submit(Surface, Snap(original)));
        Assert.False(first.Incremental);
        Assert.Equal(2, first.Issues.Issues.Count);

        // Insert "bad " into the middle line; the issue on line 3 must shift by 4.
        var edited = original.Replace("second line", "second bad line");
        var second = await NextResult(pipeline, () => pipeline.Submit(Surface, Snap(edited)));

        Assert.True(second.Incremental);
        Assert.Equal("second bad line", analyzer.Requests.Last().Text);
        Assert.Equal(
            AllOffsets(edited, "bad"),
            second.Issues.Issues.Select(i => i.StartOffset));
    }

    [Fact]
    public async Task IncrementalResult_MatchesFullAnalysis_ForRandomEdits()
    {
        var rng = new Random(42);
        var analyzer = new BadWordAnalyzer();
        using var pipeline = new AnalysisPipeline([analyzer], new AnalysisPipelineOptions { Debounce = TimeSpan.Zero, FullAnalysisMaxChars = 0 });

        string[] pieces = ["bad", "ok", " ", "\n", "ba", "d"];
        var text = "start bad\nmiddle\nend bad";
        await NextResult(pipeline, () => pipeline.Submit(Surface, Snap(text)));

        for (int i = 0; i < 60; i++)
        {
            int pos = rng.Next(text.Length + 1);
            int del = rng.Next(Math.Min(4, text.Length - pos) + 1);
            text = text[..pos] + pieces[rng.Next(pieces.Length)] + text[(pos + del)..];

            var snapshotText = text;
            var result = await NextResult(pipeline, () => pipeline.Submit(Surface, Snap(snapshotText)));
            Assert.Equal(AllOffsets(text, "bad"), result.Issues.Issues.Select(x => x.StartOffset));
        }
    }

    [Fact]
    public async Task SurfaceSwitch_TriggersFullAnalysis()
    {
        var analyzer = new BadWordAnalyzer();
        using var pipeline = new AnalysisPipeline([analyzer], new AnalysisPipelineOptions { Debounce = Debounce, FullAnalysisMaxChars = 0 });

        await NextResult(pipeline, () => pipeline.Submit(Surface, Snap("bad\nx")));
        var other = Surface with { SurfaceId = "s2" };
        var result = await NextResult(pipeline, () => pipeline.Submit(other, Snap("bad\ny")));

        Assert.False(result.Incremental);
        Assert.Equal("bad\ny", analyzer.Requests.Last().Text);
    }

    [Fact]
    public async Task FailingAnalyzer_DoesNotBlockOthers()
    {
        var good = new BadWordAnalyzer();
        using var pipeline = new AnalysisPipeline([new ThrowingAnalyzer(), good], new AnalysisPipelineOptions { Debounce = Debounce });

        var result = await NextResult(pipeline, () => pipeline.Submit(Surface, Snap("bad")));
        Assert.Single(result.Issues.Issues);
    }

    [Fact]
    public async Task UnavailableAnalyzer_IsSkipped()
    {
        var unavailable = new BadWordAnalyzer { IsAvailable = false };
        using var pipeline = new AnalysisPipeline([unavailable], new AnalysisPipelineOptions { Debounce = Debounce });

        var result = await NextResult(pipeline, () => pipeline.Submit(Surface, Snap("bad")));
        Assert.Empty(result.Issues.Issues);
        Assert.Empty(unavailable.Requests);
    }

    [Fact]
    public async Task Clear_CancelsPendingRun()
    {
        var analyzer = new BadWordAnalyzer();
        using var pipeline = new AnalysisPipeline([analyzer], new AnalysisPipelineOptions { Debounce = TimeSpan.FromMilliseconds(150) });

        int completed = 0;
        pipeline.AnalysisCompleted += (_, _) => Interlocked.Increment(ref completed);
        pipeline.Submit(Surface, Snap("bad"));
        pipeline.Clear();

        await Task.Delay(400);
        Assert.Equal(0, completed);
        Assert.Empty(analyzer.Requests);
    }

    private static IEnumerable<int> AllOffsets(string text, string word)
    {
        for (int i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.Ordinal))
            yield return i;
    }
}
