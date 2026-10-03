using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Text;

namespace Redline.Core.Pipeline;

public sealed record AnalysisPipelineOptions
{
    /// <summary>Quiet period after the last submitted snapshot before analysis starts.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Documents up to this length are re-analyzed in full on every change; larger ones incrementally by paragraph.</summary>
    public int FullAnalysisMaxChars { get; init; } = 20_000;

    public string Language { get; init; } = "en-US";
}

public sealed record AnalysisResult(
    TextSurfaceContext Surface,
    TextSnapshot Snapshot,
    IssueSet Issues,
    TextRange AnalyzedRange,
    bool Incremental,
    TimeSpan Duration,
    IReadOnlyDictionary<string, TimeSpan> AnalyzerDurations);

/// <summary>
/// Debounces snapshots, cancels superseded work, runs all available analyzers over the
/// affected context, and merges results into a per-surface <see cref="IssueSet"/>.
/// </summary>
/// <remarks>
/// Concurrency model: <see cref="Submit"/> may be called from any thread. Each submit cancels
/// the previous pending run. Runs are serialized by <c>_runGate</c>, and all analysis state
/// (<c>_lastSurfaceId</c>, <c>_lastText</c>, <c>_issues</c>) is read and written only while
/// holding it, so a diff is always computed against the last *committed* analysis, never
/// against a snapshot whose analysis was cancelled.
/// </remarks>
public sealed class AnalysisPipeline : IDisposable
{
    private readonly IReadOnlyList<ITextAnalyzer> _analyzers;
    private readonly AnalysisPipelineOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _submitLock = new();

    private CancellationTokenSource _pendingCts = new();
    private bool _disposed;

    // Guarded by _runGate.
    private string? _lastSurfaceId;
    private string? _lastText;
    private IssueSet _issues = IssueSet.Empty;

    public AnalysisPipeline(
        IEnumerable<ITextAnalyzer> analyzers,
        AnalysisPipelineOptions? options = null,
        ILogger<AnalysisPipeline>? logger = null)
    {
        _analyzers = analyzers.ToList();
        _options = options ?? new AnalysisPipelineOptions();
        _logger = logger ?? NullLogger<AnalysisPipeline>.Instance;
    }

    /// <summary>Raised on a thread-pool thread after each committed analysis.</summary>
    public event EventHandler<AnalysisResult>? AnalysisCompleted;

    public IReadOnlyList<ITextAnalyzer> Analyzers => _analyzers;

    /// <summary>Schedules analysis of <paramref name="snapshot"/>, superseding any pending request.</summary>
    public void Submit(TextSurfaceContext surface, TextSnapshot snapshot)
    {
        CancellationToken token;
        lock (_submitLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pendingCts.Cancel();
            _pendingCts = new CancellationTokenSource();
            token = _pendingCts.Token;
        }

        _ = RunAsync(surface, snapshot, token);
    }

    /// <summary>Cancels pending work and forgets all analysis state (e.g. focus left any text surface).</summary>
    public void Clear()
    {
        CancellationToken token;
        lock (_submitLock)
        {
            if (_disposed) return;
            _pendingCts.Cancel();
            _pendingCts = new CancellationTokenSource();
            token = _pendingCts.Token;
        }

        _ = ClearStateAsync(token);
    }

    private async Task ClearStateAsync(CancellationToken token)
    {
        try
        {
            await _runGate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // a newer Submit/Clear superseded us; it owns the state now
        }

        try
        {
            _lastSurfaceId = null;
            _lastText = null;
            _issues = IssueSet.Empty;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task RunAsync(TextSurfaceContext surface, TextSnapshot snapshot, CancellationToken token)
    {
        try
        {
            await Task.Delay(_options.Debounce, token).ConfigureAwait(false);
            await _runGate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            var result = await AnalyzeAsync(surface, snapshot, token).ConfigureAwait(false);
            if (result is not null)
                AnalysisCompleted?.Invoke(this, result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Analysis run failed for {Process}", surface.ProcessName);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>Must be called while holding <c>_runGate</c>.</summary>
    private async Task<AnalysisResult?> AnalyzeAsync(TextSurfaceContext surface, TextSnapshot snapshot, CancellationToken token)
    {
        var text = snapshot.Text;
        bool sameSurface = _lastSurfaceId == surface.SurfaceId && _lastText is not null;

        if (sameSurface && string.Equals(_lastText, text, StringComparison.Ordinal))
            return null;

        bool incremental = sameSurface && text.Length > _options.FullAnalysisMaxChars;
        TextRange region = new(0, text.Length);
        TextRange oldRegion = new(0, _lastText?.Length ?? 0);

        if (incremental)
        {
            var change = TextDiff.Compute(_lastText!, text);
            region = ContextExpander.ToParagraph(text, change.NewRange);
            // Map the expanded region back into old-text coordinates. The expansion only
            // grows outward over unchanged text, so its tail beyond the change is unchanged too.
            oldRegion = new TextRange(region.Start, region.Length - change.Delta);
        }

        var request = new TextAnalysisRequest
        {
            Text = text.Substring(region.Start, region.Length),
            ContextOffset = region.Start,
            SnapshotVersion = snapshot.Version,
            Language = _options.Language,
        };

        var sw = Stopwatch.StartNew();
        var available = _analyzers.Where(a => a.IsAvailable).ToList();
        var timed = await Task.WhenAll(available.Select(a => RunAnalyzerAsync(a, request, token))).ConfigureAwait(false);
        sw.Stop();

        token.ThrowIfCancellationRequested();

        var fresh = timed.SelectMany(t => t.Issues);
        _issues = incremental
            ? _issues.Rebase(oldRegion, region.Length, fresh, snapshot.Version)
            : IssueSet.From(fresh, snapshot.Version);
        _lastSurfaceId = surface.SurfaceId;
        _lastText = text;

        _logger.LogDebug(
            "Analyzed v{Version} ({Mode}, {RegionLength}/{TextLength} chars) in {Ms:F1} ms: {IssueCount} issues",
            snapshot.Version, incremental ? "incremental" : "full", region.Length, text.Length,
            sw.Elapsed.TotalMilliseconds, _issues.Issues.Count);

        return new AnalysisResult(
            surface, snapshot, _issues, region, incremental, sw.Elapsed,
            timed.ToDictionary(t => t.Name, t => t.Duration));
    }

    private async Task<(string Name, IReadOnlyList<TextIssue> Issues, TimeSpan Duration)> RunAnalyzerAsync(
        ITextAnalyzer analyzer, TextAnalysisRequest request, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var issues = await analyzer.AnalyzeAsync(request, token).ConfigureAwait(false);
            return (analyzer.Name, issues, sw.Elapsed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One broken analyzer must not take down the others.
            _logger.LogWarning(ex, "Analyzer {Analyzer} failed", analyzer.Name);
            return (analyzer.Name, Array.Empty<TextIssue>(), sw.Elapsed);
        }
    }

    public void Dispose()
    {
        lock (_submitLock)
        {
            if (_disposed) return;
            _disposed = true;
            _pendingCts.Cancel();
        }
        // _runGate is intentionally not disposed: an in-flight run may still Release() it.
    }
}
