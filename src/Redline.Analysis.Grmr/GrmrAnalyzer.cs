using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Text;

namespace Redline.Analysis.Grmr;

/// <summary>
/// Grammar suggestions from the GRMR-V3 model, one sentence at a time. The model needs ~0.5 s per
/// sentence on a CPU, far too slow for the analysis pipeline's keystroke budget, so this analyzer
/// never waits for it: <see cref="AnalyzeAsync"/> returns what's cached and queues the rest for a
/// background worker, which raises <see cref="ResultsReady"/> when new suggestions exist (the app
/// then calls <c>AnalysisPipeline.Refresh</c>). Supplementary: spelling and Harper win overlaps.
/// </summary>
public sealed class GrmrAnalyzer : ITextAnalyzer, IDisposable
{
    public const int MaxSentenceChars = 500;
    public const int MaxQueuedSentences = 60;
    public const int CacheCapacity = 4000;
    public const string RuleId = AnalyzerNames.GrammarModel + ":Correction";

    /// <summary>The model's process ends this long after the last sentence; starting it again takes ~1-4 s, in the background.</summary>
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NotifyInterval = TimeSpan.FromSeconds(1.5);

    private readonly Func<string?> _modelPath;
    private readonly Func<string, ISentenceCorrector> _createCorrector;
    private readonly ILogger _logger;
    private readonly TimeSpan _idleUnload;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _lru = new(); // most recent last
    private List<string> _queue = new();                                 // guarded by _gate
    private List<string> _priority = new();                              // guarded by _gate; asked for by a fix popup, done first
    private HashSet<string> _lastRequested = new(StringComparer.Ordinal); // guarded by _gate

    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private volatile ISentenceCorrector? _corrector; // written under _engineGate
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;
    private volatile bool _enabled;
    private volatile bool _failed;

    /// <param name="Edits">Small edits, shown as underlines (<see cref="RewriteDiff.Compute(string, string?)"/>).</param>
    /// <param name="Whole">The model's whole answer as edits (<see cref="RewriteDiff.ComputeLoose"/>), for previewed fixes only.</param>
    private sealed record CacheEntry(string Sentence, IReadOnlyList<RewriteEdit> Edits, IReadOnlyList<RewriteEdit> Whole);

    /// <param name="modelPath">The verified model path, or null while it isn't installed.</param>
    /// <param name="createCorrector">Loads the model (slow; called on the worker thread).</param>
    public GrmrAnalyzer(Func<string?> modelPath, Func<string, ISentenceCorrector> createCorrector,
        ILogger<GrmrAnalyzer>? logger = null, TimeSpan? idleUnload = null)
    {
        _modelPath = modelPath;
        _createCorrector = createCorrector;
        _logger = logger ?? NullLogger<GrmrAnalyzer>.Instance;
        _idleUnload = idleUnload ?? IdleUnload;
    }

    public string Name => AnalyzerNames.GrammarModel;
    public bool IsSupplementary => true;
    public bool IsAvailable => _enabled && !_failed && _modelPath() is not null;

    /// <summary>The user setting. Turning it off drops queued work and frees the model's memory.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value) return;
            lock (_gate) { _queue = new(); _priority = new(); }
            _ = ReleaseModelAsync();
        }
    }

    /// <summary>True once loading the runtime or model failed; stays off until restart.</summary>
    public bool Failed => _failed;

    /// <summary>Where the loaded model runs ("CPU", "GPU: name"), or null while it isn't loaded.</summary>
    public string? Device => _corrector?.Device;

    /// <summary>New suggestions are cached for text that was analyzed before they existed. Background thread.</summary>
    public event Action? ResultsReady;

    public Task<IReadOnlyList<TextIssue>> AnalyzeAsync(TextAnalysisRequest request, CancellationToken ct)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(request.Text))
            return Task.FromResult<IReadOnlyList<TextIssue>>(Array.Empty<TextIssue>());

        var issues = new List<TextIssue>();
        var missing = new List<string>();
        var requested = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var range in SentenceSplitter.Split(request.Text))
            {
                if (range.Length > MaxSentenceChars) continue;
                var sentence = request.Text.Substring(range.Start, range.Length);
                requested.Add(sentence);
                if (_cache.TryGetValue(sentence, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddLast(node);
                    issues.AddRange(ToIssues(node.Value.Edits, sentence, request.ContextOffset + range.Start, request.SnapshotVersion));
                }
                else
                {
                    missing.Add(sentence);
                }
            }

            // Sentences that weren't in the previous request were just typed or edited: do those first.
            var previous = _lastRequested;
            _queue = missing.Where(s => !previous.Contains(s))
                .Concat(missing.Where(previous.Contains))
                .Distinct(StringComparer.Ordinal)
                .Take(MaxQueuedSentences)
                .ToList();
            _lastRequested = requested;
        }

        if (missing.Count > 0)
        {
            EnsureWorker();
            _signal.Release();
        }
        return Task.FromResult<IReadOnlyList<TextIssue>>(issues);
    }

    /// <summary>
    /// The model's whole-sentence answer for each sentence of <paramref name="text"/> inside <paramref name="scope"/>
    /// that it would change (edits relative to the sentence). Sentences it hasn't seen jump the queue; waits up to
    /// <paramref name="wait"/> for them and leaves out the ones still pending.
    /// </summary>
    public async Task<IReadOnlyList<(TextRange Sentence, IReadOnlyList<RewriteEdit> Edits)>> GetSentenceAlternativesAsync(
        string text, TextRange scope, TimeSpan wait, CancellationToken ct)
    {
        if (!IsAvailable) return [];
        var sentences = SentenceSplitter.Split(text)
            .Where(r => r.Start >= scope.Start && r.End <= scope.End && r.Length <= MaxSentenceChars)
            .Select(r => (Range: r, Text: text.Substring(r.Start, r.Length)))
            .ToList();

        lock (_gate)
        {
            var missing = sentences.Select(x => x.Text).Where(t => !_cache.ContainsKey(t)).Distinct(StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                _priority = missing;
                EnsureWorker();
                _signal.Release();
            }
        }

        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            bool done;
            lock (_gate) done = sentences.All(x => _cache.ContainsKey(x.Text));
            if (done || DateTime.UtcNow >= deadline || !IsAvailable) break;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        var result = new List<(TextRange, IReadOnlyList<RewriteEdit>)>();
        lock (_gate)
        {
            foreach (var (range, sentence) in sentences)
            {
                if (_cache.TryGetValue(sentence, out var node) && node.Value.Whole.Count > 0)
                    result.Add((range, node.Value.Whole));
            }
        }
        return result;
    }

    /// <summary>Unloads the model (e.g. before deleting its file). It reloads on demand.</summary>
    public async Task ReleaseModelAsync()
    {
        await _engineGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_corrector is null) return;
            _corrector.Dispose();
            _corrector = null;
            _logger.LogInformation("Grammar model unloaded");
        }
        finally
        {
            _engineGate.Release();
        }
    }

    internal static IEnumerable<TextIssue> ToIssues(IReadOnlyList<RewriteEdit> edits, string sentence, int offset, long version)
    {
        foreach (var edit in edits)
        {
            var original = sentence.Substring(edit.Start, edit.Length);
            var (category, message) = Describe(original, edit.Replacement);
            yield return new TextIssue
            {
                StartOffset = offset + edit.Start,
                Length = edit.Length,
                OriginalText = original,
                Category = category,
                Message = message,
                Suggestions = [edit.Replacement],
                Analyzer = AnalyzerNames.GrammarModel,
                RuleId = RuleId,
                Confidence = 0.8,
                SnapshotVersion = version,
            };
        }
    }

    private static (IssueCategory, string) Describe(string original, string replacement)
    {
        string a = new(original.Where(char.IsLetterOrDigit).ToArray());
        string b = new(replacement.Where(char.IsLetterOrDigit).ToArray());
        if (a == b) return (IssueCategory.Punctuation, "Punctuation (suggested by the grammar model).");
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return (IssueCategory.Grammar, "Capitalization (suggested by the grammar model).");
        if (replacement.Trim().Length == 0) return (IssueCategory.Grammar, "This word may not be needed (suggested by the grammar model).");
        return (IssueCategory.Grammar, "Possible grammar error (suggested by the grammar model).");
    }

    private void EnsureWorker()
    {
        lock (_gate)
            _worker ??= Task.Factory.StartNew(() => WorkLoopAsync(_shutdown.Token), _shutdown.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private async Task WorkLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await _signal.WaitAsync(_idleUnload, ct).ConfigureAwait(false))
                {
                    await ReleaseModelAsync().ConfigureAwait(false); // idle: give the memory back
                    continue;
                }
                await DrainQueueAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Grammar model worker failed");
            }
        }
    }

    private async Task DrainQueueAsync(CancellationToken ct)
    {
        bool pending = false;
        var sinceNotify = Stopwatch.StartNew();
        int done = 0, withEdits = 0;
        var batch = Stopwatch.StartNew();

        while (_enabled && !_failed && TryDequeue(out var sentence))
        {
            var answer = await CorrectAsync(sentence, ct).ConfigureAwait(false);
            if (answer is null) break; // the model couldn't be loaded
            done++;

            lock (_gate) AddToCache(answer);
            if (answer.Edits.Count > 0) { pending = true; withEdits++; }

            bool queueEmpty;
            lock (_gate) queueEmpty = _queue.Count == 0 && _priority.Count == 0;
            if (pending && (queueEmpty || sinceNotify.Elapsed >= NotifyInterval))
            {
                pending = false;
                sinceNotify.Restart();
                ResultsReady?.Invoke();
            }
        }

        if (pending) ResultsReady?.Invoke();
        if (done > 0)
            _logger.LogDebug("Grammar model checked {Count} sentences in {Ms:F0} ms ({WithEdits} with suggestions)",
                done, batch.Elapsed.TotalMilliseconds, withEdits);
    }

    private bool TryDequeue(out string sentence)
    {
        lock (_gate)
        {
            foreach (var queue in new[] { _priority, _queue })
            {
                while (queue.Count > 0)
                {
                    sentence = queue[0];
                    queue.RemoveAt(0);
                    if (!_cache.ContainsKey(sentence)) return true;
                }
            }
        }
        sentence = string.Empty;
        return false;
    }

    /// <summary>The model's answer for one sentence (no edits when it isn't usable), or null if the model can't load.</summary>
    private async Task<CacheEntry?> CorrectAsync(string sentence, CancellationToken ct)
    {
        await _engineGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_corrector is null)
            {
                var path = _modelPath();
                if (path is null) return null;
                var sw = Stopwatch.StartNew();
                try
                {
                    _corrector = _createCorrector(path);
                }
                catch (Exception ex)
                {
                    _failed = true;
                    lock (_gate) { _queue = new(); _priority = new(); }
                    _logger.LogError(ex, "Couldn't load the grammar model; AI grammar checking is off until Redline restarts");
                    return null;
                }
                _logger.LogInformation("Grammar model loaded in {Ms:F0} ms", sw.Elapsed.TotalMilliseconds);
            }

            try
            {
                var corrected = await _corrector.CorrectAsync(sentence, ct).ConfigureAwait(false);
                return new CacheEntry(sentence, RewriteDiff.Compute(sentence, corrected), RewriteDiff.ComputeLoose(sentence, corrected));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (GrammarModelLoadException ex)
            {
                _corrector.Dispose();
                _corrector = null;
                _failed = true;
                lock (_gate) { _queue = new(); _priority = new(); }
                _logger.LogError(ex, "The grammar model stopped working; AI grammar checking is off until Redline restarts");
                return null;
            }
            catch (Exception ex)
            {
                // Cache the empty result: retrying the same sentence would fail the same way.
                _logger.LogWarning("Grammar model failed on a sentence: {Reason}", ex.GetType().Name);
                return new CacheEntry(sentence, [], []);
            }
        }
        finally
        {
            _engineGate.Release();
        }
    }

    /// <summary>Caller holds _gate.</summary>
    private void AddToCache(CacheEntry entry)
    {
        if (_cache.ContainsKey(entry.Sentence)) return;
        _cache[entry.Sentence] = _lru.AddLast(entry);
        while (_lru.Count > CacheCapacity)
        {
            _cache.Remove(_lru.First!.Value.Sentence);
            _lru.RemoveFirst();
        }
    }

    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested) return; // registered twice in DI, so disposed twice
        _shutdown.Cancel();
        // Wait briefly for an in-flight sentence so the native model isn't freed under it.
        if (_engineGate.Wait(TimeSpan.FromSeconds(5)))
        {
            _corrector?.Dispose();
            _corrector = null;
            _engineGate.Release();
        }
    }
}
