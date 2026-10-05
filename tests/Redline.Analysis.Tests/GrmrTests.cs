using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Redline.Analysis.Grmr;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Xunit;

namespace Redline.Analysis.Tests;

public class GrmrPromptTests
{
    [Fact]
    public void Build_UsesTheModelsTextAndCorrectedTurns()
    {
        Assert.Equal("<start_of_turn>text\nShe go.<end_of_turn>\n<start_of_turn>corrected\n", GrmrPrompt.Build("She go."));
    }

    [Fact]
    public void ParseOutput_StopsAtEndOfTurn_AndRejectsRunaways()
    {
        Assert.Equal("She goes.", GrmrPrompt.ParseOutput("She goes.<end_of_turn>", hitTokenLimit: false));
        Assert.Equal("She goes.", GrmrPrompt.ParseOutput("She goes.\n", hitTokenLimit: false)); // stopped on the EOG token
        Assert.Null(GrmrPrompt.ParseOutput("She goes to school and then she goes", hitTokenLimit: true));
        Assert.Null(GrmrPrompt.ParseOutput("<end_of_turn>", hitTokenLimit: false));
    }
}

public sealed class GrmrAnalyzerTests : IDisposable
{
    /// <summary>Answers from a table; unknown sentences come back unchanged. Can be held to observe the queue.</summary>
    private sealed class FakeCorrector : ISentenceCorrector
    {
        public ConcurrentQueue<string> Seen { get; } = new();
        public Dictionary<string, string> Answers { get; } = new();
        public SemaphoreSlim? Hold { get; set; }
        public bool Disposed { get; private set; }

        public async Task<string?> CorrectAsync(string sentence, CancellationToken ct)
        {
            Seen.Enqueue(sentence);
            if (Hold is not null) await Hold.WaitAsync(ct);
            return Answers.TryGetValue(sentence, out var answer) ? answer : sentence;
        }

        public void Dispose() => Disposed = true;
    }

    private readonly FakeCorrector _corrector = new();
    private readonly GrmrAnalyzer _analyzer;
    private int _loads;

    public GrmrAnalyzerTests()
    {
        _analyzer = new GrmrAnalyzer(() => "model.gguf", _ => { _loads++; return _corrector; }, NullLogger<GrmrAnalyzer>.Instance) { Enabled = true };
    }

    public void Dispose() => _analyzer.Dispose();

    private static TextAnalysisRequest Request(string text, int offset = 0) => new() { Text = text, ContextOffset = offset, SnapshotVersion = 7 };

    private async Task<IReadOnlyList<TextIssue>> AnalyzeUntilReady(TextAnalysisRequest request)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action handler = () => ready.TrySetResult();
        _analyzer.ResultsReady += handler;
        try
        {
            Assert.Empty(await _analyzer.AnalyzeAsync(request, default)); // nothing cached yet: never waits for the model
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            _analyzer.ResultsReady -= handler;
        }
        return await _analyzer.AnalyzeAsync(request, default);
    }

    [Fact]
    public async Task FirstPassQueues_ThenCachedSuggestionsComeBackAsIssues()
    {
        _corrector.Answers["She go to school."] = "She goes to school.";

        var issues = await AnalyzeUntilReady(Request("Hello there. She go to school.", offset: 100));

        var issue = Assert.Single(issues);
        Assert.Equal(100 + 17, issue.StartOffset);
        Assert.Equal("go", issue.OriginalText);
        Assert.Equal(["goes"], issue.Suggestions);
        Assert.Equal(IssueCategory.Grammar, issue.Category);
        Assert.Equal(AnalyzerNames.GrammarModel, issue.Analyzer);
        Assert.Equal(GrmrAnalyzer.RuleId, issue.RuleId);
        Assert.Equal(7, issue.SnapshotVersion);
        Assert.True(_analyzer.IsSupplementary);
    }

    [Fact]
    public async Task EachSentenceIsCorrectedOnce()
    {
        _corrector.Answers["She go."] = "She goes.";
        await AnalyzeUntilReady(Request("She go."));
        await _analyzer.AnalyzeAsync(Request("She go."), default);
        await _analyzer.AnalyzeAsync(Request("Intro. She go."), default);
        await Task.Delay(200);

        Assert.Equal(1, _corrector.Seen.Count(s => s == "She go."));
        Assert.Equal(1, _loads);
    }

    [Fact]
    public async Task PunctuationOnlyFixes_AreCategorizedAsPunctuation()
    {
        _corrector.Answers["Its a nice day isnt it?"] = "It's a nice day, isn't it?";
        var issues = await AnalyzeUntilReady(Request("Its a nice day isnt it?"));
        Assert.All(issues, i => Assert.Equal(IssueCategory.Punctuation, i.Category));
        Assert.Equal(["Its", "day", "isnt"], issues.Select(i => i.OriginalText));
    }

    [Fact]
    public async Task SentenceAlternatives_IncludeAnswersTooBigForUnderlines()
    {
        const string messy = "i has went too the stor yesterday an buyed sum food";
        _corrector.Answers[messy] = "I had gone to the store yesterday and bought some food.";
        _corrector.Answers["She go."] = "She goes.";
        var text = "Fine here. " + messy + "\nShe go.";

        var answers = await _analyzer.GetSentenceAlternativesAsync(text, new TextRange(0, text.Length), TimeSpan.FromSeconds(5), default);

        Assert.Equal([11, text.Length - 7], answers.Select(a => a.Sentence.Start));
        Assert.True(answers[0].Edits.Count > 6);
        // Underlines still come from the strict diff only.
        var issues = await _analyzer.AnalyzeAsync(Request(text), default);
        Assert.Equal(["go"], issues.Select(i => i.OriginalText));
    }

    [Fact]
    public async Task SentenceAlternatives_JumpTheQueue_AndGiveUpAfterTheWait()
    {
        _corrector.Hold = new SemaphoreSlim(0);
        await _analyzer.AnalyzeAsync(Request("One here. Two here. Three here. Four here."), default);
        await WaitUntil(() => _corrector.Seen.Count == 1); // "One here." is in progress

        var none = await _analyzer.GetSentenceAlternativesAsync("Z go.", new TextRange(0, 5), TimeSpan.FromMilliseconds(100), default);
        Assert.Empty(none); // still pending: the popup shows what it has

        _corrector.Answers["Z go."] = "Z goes.";
        _corrector.Hold.Release(10);
        var answers = await _analyzer.GetSentenceAlternativesAsync("Z go.", new TextRange(0, 5), TimeSpan.FromSeconds(5), default);

        Assert.Single(answers);
        Assert.Equal("Z go.", _corrector.Seen.ElementAt(1));
    }

    [Fact]
    public async Task UnchangedSentences_DoNotRaiseResultsReady()
    {
        int raised = 0;
        _analyzer.ResultsReady += () => Interlocked.Increment(ref raised);
        await _analyzer.AnalyzeAsync(Request("All good here. Nothing to fix."), default);
        await WaitUntil(() => _corrector.Seen.Count == 2);
        await Task.Delay(100);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task NewRequestsReplaceTheQueue_EditedSentencesFirst()
    {
        _corrector.Hold = new SemaphoreSlim(0);
        await _analyzer.AnalyzeAsync(Request("One here. Two here. Three here."), default);
        await WaitUntil(() => _corrector.Seen.Count == 1); // "One here." is in progress

        // The user edited the last sentence: it jumps ahead of the unchanged ones.
        await _analyzer.AnalyzeAsync(Request("One here. Two here. Three changed."), default);
        _corrector.Hold.Release(10);
        await WaitUntil(() => _corrector.Seen.Count == 3);

        Assert.Equal(["One here.", "Three changed.", "Two here."], _corrector.Seen);
    }

    [Fact]
    public async Task Disabled_ReturnsNothing_AndReleasesTheModel()
    {
        _corrector.Answers["She go."] = "She goes.";
        await AnalyzeUntilReady(Request("She go."));
        _analyzer.Enabled = false;
        await WaitUntil(() => _corrector.Disposed);

        Assert.False(_analyzer.IsAvailable);
        Assert.Empty(await _analyzer.AnalyzeAsync(Request("She go."), default));
    }

    [Fact]
    public async Task ModelThatFailsToLoad_TurnsTheAnalyzerOff()
    {
        using var analyzer = new GrmrAnalyzer(() => "missing.gguf", _ => throw new InvalidOperationException("no runtime")) { Enabled = true };
        await analyzer.AnalyzeAsync(Request("She go."), default);
        await WaitUntil(() => analyzer.Failed);
        Assert.False(analyzer.IsAvailable);
    }

    [Fact]
    public void NoModel_IsUnavailable()
    {
        using var analyzer = new GrmrAnalyzer(() => null, _ => throw new InvalidOperationException()) { Enabled = true };
        Assert.False(analyzer.IsAvailable);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }
}

public sealed class GrmrModelStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"redline-models-{Guid.NewGuid():N}");
    private static readonly byte[] Content = Encoding.ASCII.GetBytes(new string('x', 100_000));

    private static ModelFile Spec(byte[] content) => new("test.gguf", "https://example.invalid/test.gguf", content.Length,
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());

    /// <summary>Serves <c>body</c>, honouring a Range header when <c>supportRange</c>.</summary>
    private sealed class FakeServer(byte[] body, bool supportRange = true) : HttpMessageHandler
    {
        public List<long?> RangeStarts { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            long? from = request.Headers.Range?.Ranges.First().From;
            RangeStarts.Add(from);
            if (from is { } start && supportRange)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body[(int)start..]) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task<ModelState> Finished(GrmrModelStore store)
    {
        var done = new TaskCompletionSource<ModelState>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += () => { if (store.State != ModelState.Downloading) done.TrySetResult(store.State); };
        store.StartDownload();
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Download_VerifiesAndInstalls()
    {
        using var store = new GrmrModelStore(_dir, "test", handler: new FakeServer(Content), file: Spec(Content));
        Assert.Equal(ModelState.NotInstalled, store.State);
        Assert.Null(store.InstalledPath);

        Assert.Equal(ModelState.Installed, await Finished(store));
        Assert.Equal(store.ModelPath, store.InstalledPath);
        Assert.Equal(Content, await File.ReadAllBytesAsync(store.ModelPath));
        Assert.False(File.Exists(store.ModelPath + ".partial"));

        // A new store (next start) sees it as installed without downloading again.
        using var again = new GrmrModelStore(_dir, "test", handler: new FakeServer([]), file: Spec(Content));
        Assert.Equal(ModelState.Installed, again.State);
    }

    [Fact]
    public async Task Download_ResumesFromAPartialFile()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "test.gguf.partial"), Content[..40_000]);
        var server = new FakeServer(Content);
        using var store = new GrmrModelStore(_dir, "test", handler: server, file: Spec(Content));

        Assert.Equal(ModelState.Installed, await Finished(store));
        Assert.Equal([40_000L], server.RangeStarts);
        Assert.Equal(Content, await File.ReadAllBytesAsync(store.ModelPath));
    }

    [Fact]
    public async Task Download_StartsOverWhenTheServerIgnoresTheRange()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "test.gguf.partial"), Content[..40_000]);
        using var store = new GrmrModelStore(_dir, "test", handler: new FakeServer(Content, supportRange: false), file: Spec(Content));

        Assert.Equal(ModelState.Installed, await Finished(store));
        Assert.Equal(Content, await File.ReadAllBytesAsync(store.ModelPath));
    }

    [Fact]
    public async Task ChecksumMismatch_FailsAndKeepsNothing()
    {
        var tampered = (byte[])Content.Clone();
        tampered[500] = (byte)'y';
        using var store = new GrmrModelStore(_dir, "test", handler: new FakeServer(tampered), file: Spec(Content));

        Assert.Equal(ModelState.Failed, await Finished(store));
        Assert.Contains("checksum", store.Error);
        Assert.False(File.Exists(store.ModelPath));
        Assert.False(File.Exists(store.ModelPath + ".partial"));
    }

    [Fact]
    public async Task Remove_DeletesTheModel()
    {
        using var store = new GrmrModelStore(_dir, "test", handler: new FakeServer(Content), file: Spec(Content));
        await Finished(store);

        Assert.True(store.Remove());
        Assert.Equal(ModelState.NotInstalled, store.State);
        Assert.False(File.Exists(store.ModelPath));
    }
}

public sealed class GpuGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"redline-gpu-{Guid.NewGuid():N}");

    [Fact]
    public void CrashDuringTrial_BlocksTheGpuNextTime_UntilReset()
    {
        var first = new GpuGuard(_dir);
        Assert.False(first.Blocked);
        first.BeginTrial();
        // The process dies here: no EndTrial, no Dispose.

        var second = new GpuGuard(_dir);
        Assert.True(second.CrashedLastTime);
        Assert.True(second.Blocked);

        var third = new GpuGuard(_dir);
        Assert.False(third.CrashedLastTime); // reported once
        Assert.True(third.Blocked);          // but still blocked

        third.Reset();
        Assert.False(third.Blocked);
        Assert.False(new GpuGuard(_dir).Blocked);
    }

    [Fact]
    public void SuccessfulTrial_OrCleanExit_LeavesTheGpuOn()
    {
        var guard = new GpuGuard(_dir);
        guard.BeginTrial();
        guard.EndTrial();
        Assert.True(guard.TrialPassed);
        Assert.False(new GpuGuard(_dir).Blocked);

        var exiting = new GpuGuard(_dir);
        exiting.BeginTrial();
        exiting.Dispose(); // Redline closed while the model was loading
        var next = new GpuGuard(_dir);
        Assert.False(next.CrashedLastTime);
        Assert.False(next.Blocked);
    }

    [Fact]
    public void AfterTheGpuWorked_LaterLoadsSkipTheMarker()
    {
        var guard = new GpuGuard(_dir);
        guard.BeginTrial();
        guard.EndTrial();
        guard.BeginTrial(); // model reloaded after idle unload
        Assert.False(File.Exists(Path.Combine(_dir, GpuGuard.TrialFile)));
    }

    [Theory]
    [InlineData("llama_model_load_from_file_impl: using device Vulkan0 (Intel(R) Iris(R) Xe Graphics) - 8053 MiB free", "Intel(R) Iris(R) Xe Graphics")]
    [InlineData("llama_model_load_from_file_impl: using device Vulkan0 (NVIDIA GeForce RTX 4070) - 11800 MiB free", "NVIDIA GeForce RTX 4070")]
    public void DeviceName_IsReadFromLlamaLog(string line, string expected) =>
        Assert.Equal(expected, LlamaSentenceCorrector.DeviceLine.Match(line).Groups["name"].Value);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

/// <summary>
/// Runs the real model when REDLINE_GRMR_MODEL points at GRMR-V3-G1B-Q4_K_M.gguf (skipped otherwise:
/// the file is 806 MB and isn't in the repository). On the GPU when there is one; REDLINE_GRMR_GPU=0 forces the CPU.
/// </summary>
public class GrmrModelIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly string? ModelPath = Environment.GetEnvironmentVariable("REDLINE_GRMR_MODEL");

    [Fact]
    public async Task RealModel_FixesGrammar_AndLeavesCorrectTextAlone()
    {
        if (string.IsNullOrEmpty(ModelPath) || !File.Exists(ModelPath))
            return; // not configured on this machine

        bool gpu = Environment.GetEnvironmentVariable("REDLINE_GRMR_GPU") != "0";
        using var corrector = LlamaSentenceCorrector.Create(ModelPath, gpu, new GpuGuard(null), NullLogger.Instance);
        output.WriteLine($"Running on {corrector.Device}");
        Assert.Equal("She goes to school every day.", await corrector.CorrectAsync("She go to school every day.", default));
        Assert.Equal("The results were better than expected.", await corrector.CorrectAsync("The results was better then expected.", default));
        const string fine = "Please review the attached document and let me know if you have any questions.";
        Assert.Equal(fine, await corrector.CorrectAsync(fine, default));
    }
}
