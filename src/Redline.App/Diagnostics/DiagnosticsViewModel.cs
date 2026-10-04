using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Redline.App.Logging;
using Redline.Core.Diagnostics;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Pipeline;
using Redline.Windows;

namespace Redline.App.Diagnostics;

public sealed record IssueRow(string Category, int Offset, int Length, string Text, string Message, string Suggestions, string Analyzer, TextIssue Issue);

/// <summary>
/// Collects tracker/pipeline events for the diagnostics window. Lives for the whole app so
/// the window shows current state whenever it is opened. All mutations hop to the UI thread.
/// </summary>
public sealed class DiagnosticsViewModel : INotifyPropertyChanged
{
    private const int MaxLogLines = 300;
    private readonly Dispatcher _ui;
    private readonly PerfCounters _perfCounters;

    private string _surface = "No surface";
    private string _capabilities = string.Empty;
    private string _snapshot = string.Empty;
    private string _analysis = string.Empty;
    private string _perf = string.Empty;
    private long _latestSnapshotVersion;
    private string? _surfaceId;

    public DiagnosticsViewModel(
        Dispatcher ui,
        SurfaceTracker tracker,
        AnalysisPipeline pipeline,
        IssueCacheManager cache,
        IEnumerable<ITextAnalyzer> analyzers,
        DiagnosticsLog log,
        PerfCounters perf)
    {
        _ui = ui;
        _perfCounters = perf;

        Analyzers = string.Join("   ", analyzers.Select(a => $"{a.Name}: {(a.IsAvailable ? "available" : "UNAVAILABLE")}"));

        foreach (var entry in log.Snapshot())
            Log.Add(entry.ToString());

        log.EntryAdded += entry => Post(() =>
        {
            while (Log.Count >= MaxLogLines) Log.RemoveAt(0);
            Log.Add(entry.ToString());
        });

        tracker.SurfaceChanged += (_, e) => Post(() =>
        {
            _latestSnapshotVersion = 0;
            _surfaceId = e.Surface?.SurfaceId;
            Surface = e.Surface is null ? $"None — {e.Reason}" : e.Surface.ToString();
            Capabilities = e.Capabilities is null ? string.Empty : Describe(e.Capabilities);
            Snapshot = string.Empty;
            Analysis = string.Empty;
            Issues.Clear();
        });

        tracker.SnapshotChanged += (_, e) => Post(() =>
        {
            _latestSnapshotVersion = e.Snapshot.Version;
            Snapshot = $"v{e.Snapshot.Version}, {e.Snapshot.Length:N0} chars" +
                       (e.Change is { } c ? $", edit at {c.Start} (-{c.OldLength}/+{c.NewLength})" : " (initial read)");
        });

        pipeline.AnalysisCompleted += (_, r) => Post(() => ShowResult(r));

        // The issue list comes from the cache so dictionary/ignore decisions show immediately.
        cache.IssuesChanged += (_, e) => Post(() =>
        {
            if (e.SurfaceId == _surfaceId) ShowIssues(e.Issues);
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<IssueRow> Issues { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    public string Analyzers { get; }

    public string Surface { get => _surface; private set => Set(ref _surface, value); }
    public string Capabilities { get => _capabilities; private set => Set(ref _capabilities, value); }
    public string Snapshot { get => _snapshot; private set => Set(ref _snapshot, value); }
    public string Analysis { get => _analysis; private set => Set(ref _analysis, value); }
    public string Perf { get => _perf; private set => Set(ref _perf, value); }

    /// <summary>Re-reads the timing counters (the window calls this on a timer while open).</summary>
    public void RefreshPerf()
    {
        var summary = _perfCounters.Summary();
        Perf = summary.Length == 0 ? "No samples yet" : $"p50/p95/max — {summary}";
    }

    private void ShowResult(AnalysisResult r)
    {
        var timings = string.Join(", ", r.AnalyzerDurations.Select(kv => $"{kv.Key} {kv.Value.TotalMilliseconds:F1} ms"));
        bool stale = r.Snapshot.Version < _latestSnapshotVersion;
        Analysis = $"v{r.Snapshot.Version} {(r.Incremental ? "incremental" : "full")} " +
                   $"[{r.AnalyzedRange.Start}..{r.AnalyzedRange.End}) in {r.Duration.TotalMilliseconds:F1} ms ({timings}) — " +
                   $"{r.Issues.Issues.Count} issue(s) before filters{(stale ? " — newer edit pending" : string.Empty)}";

    }

    private void ShowIssues(IssueSet set)
    {
        Issues.Clear();
        foreach (var i in set.Issues)
        {
            Issues.Add(new IssueRow(
                i.Category.ToString(), i.StartOffset, i.Length, i.OriginalText, i.Message,
                string.Join(" | ", i.Suggestions.Select(s => s.Length == 0 ? "(remove)" : s)),
                i.Analyzer, i));
        }
    }

    private static string Describe(TextSurfaceCapabilities c) =>
        $"Patterns: {string.Join(", ", c.SupportedPatterns)} · read {Flag(c.CanReadText)} · caret {Flag(c.CanGetCaretPosition)} · " +
        $"geometry {Flag(c.CanGetBoundingRectangles)} · replace {Flag(c.CanReplaceText)}";

    private static string Flag(bool b) => b ? "✓" : "✗";

    private void Post(Action action) => _ui.BeginInvoke(action);

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
