using Redline.Core.Corrections;
using Redline.Core.Interfaces;
using Redline.Core.Models;

namespace Redline.Core.Pipeline;

public sealed record IssuesChangedEventArgs(string SurfaceId, IssueSet Issues);

/// <summary>
/// The latest analysis per surface, with user filters (personal dictionary, ignores) applied on
/// read. Keeping recent surfaces means switching back to an app shows its issues again without
/// waiting for re-analysis; the snapshot version on each entry lets callers reject stale offsets.
/// Thread-safe.
/// </summary>
public sealed class IssueCacheManager
{
    private readonly IPersonalDictionary _dictionary;
    private readonly IgnoreList _ignores;
    private readonly int _capacity;
    private readonly object _gate = new();

    // Most recently updated last.
    private readonly LinkedList<(string SurfaceId, IssueSet Raw)> _entries = new();

    public IssueCacheManager(IPersonalDictionary dictionary, IgnoreList ignores, int capacity = 16)
    {
        _dictionary = dictionary;
        _ignores = ignores;
        _capacity = capacity;

        // Filter changes don't need re-analysis: re-publish every cached surface through the new filters.
        _dictionary.Changed += RepublishAll;
        _ignores.Changed += RepublishAll;
    }

    /// <summary>Raised (on the caller's thread) with the filtered issues whenever a surface's set changes.</summary>
    public event EventHandler<IssuesChangedEventArgs>? IssuesChanged;

    public void Update(string surfaceId, IssueSet issues)
    {
        lock (_gate)
        {
            var existing = Find(surfaceId);
            if (existing is not null)
            {
                // Results can arrive out of order across surfaces, never within one; still, never go backwards.
                if (existing.Value.Raw.SnapshotVersion > issues.SnapshotVersion)
                    return;
                _entries.Remove(existing);
            }

            _entries.AddLast((surfaceId, issues));
            while (_entries.Count > _capacity)
                _entries.RemoveFirst();
        }

        IssuesChanged?.Invoke(this, new IssuesChangedEventArgs(surfaceId, Filter(issues)));
    }

    /// <summary>
    /// Filtered issues for <paramref name="surfaceId"/>, or null if none are cached or, when
    /// <paramref name="requiredVersion"/> is given, if they were computed for a different snapshot.
    /// </summary>
    public IssueSet? Get(string surfaceId, long? requiredVersion = null)
    {
        IssueSet raw;
        lock (_gate)
        {
            var node = Find(surfaceId);
            if (node is null) return null;
            raw = node.Value.Raw;
        }

        if (requiredVersion is { } v && raw.SnapshotVersion != v)
            return null;
        return Filter(raw);
    }

    public void Remove(string surfaceId)
    {
        lock (_gate)
        {
            var node = Find(surfaceId);
            if (node is not null) _entries.Remove(node);
        }
    }

    private void RepublishAll()
    {
        List<(string, IssueSet)> all;
        lock (_gate) all = _entries.ToList();
        foreach (var (surfaceId, raw) in all)
            IssuesChanged?.Invoke(this, new IssuesChangedEventArgs(surfaceId, Filter(raw)));
    }

    private IssueSet Filter(IssueSet raw)
    {
        var kept = raw.Issues.Where(i => !IsFilteredOut(i)).ToList();
        return kept.Count == raw.Issues.Count ? raw : new IssueSet(kept, raw.SnapshotVersion);
    }

    private bool IsFilteredOut(TextIssue issue) =>
        (issue.Category == IssueCategory.Spelling && _dictionary.Contains(issue.OriginalText)) ||
        _ignores.IsIgnored(issue);

    /// <summary>Caller holds _gate.</summary>
    private LinkedListNode<(string SurfaceId, IssueSet Raw)>? Find(string surfaceId)
    {
        for (var node = _entries.Last; node is not null; node = node.Previous)
        {
            if (node.Value.SurfaceId == surfaceId) return node;
        }
        return null;
    }
}
