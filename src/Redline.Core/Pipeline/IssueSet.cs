using Redline.Core.Models;

namespace Redline.Core.Pipeline;

/// <summary>
/// Immutable set of issues for one snapshot, with support for carrying unaffected issues
/// forward across an edit. Phase 2's IssueCacheManager builds on this.
/// </summary>
public sealed class IssueSet
{
    public static IssueSet Empty { get; } = new(Array.Empty<TextIssue>(), 0);

    public IReadOnlyList<TextIssue> Issues { get; }
    public long SnapshotVersion { get; }

    public IssueSet(IReadOnlyList<TextIssue> issues, long snapshotVersion)
    {
        Issues = issues;
        SnapshotVersion = snapshotVersion;
    }

    public static IssueSet From(IEnumerable<TextIssue> issues, long snapshotVersion) =>
        new(Sort(issues.Select(i => i with { SnapshotVersion = snapshotVersion })), snapshotVersion);

    /// <summary>
    /// Produces the issues that survive replacing <paramref name="oldRegion"/> (old-text
    /// coordinates) with <paramref name="newRegionLength"/> characters. Issues ending before the
    /// region are kept, issues starting at or after its end are shifted, and anything overlapping it is dropped
    /// so it can be re-analyzed. <paramref name="fresh"/> issues (new-text coordinates) are merged in.
    /// </summary>
    public IssueSet Rebase(TextRange oldRegion, int newRegionLength, IEnumerable<TextIssue> fresh, long newVersion)
    {
        int delta = newRegionLength - oldRegion.Length;
        var kept = new List<TextIssue>(Issues.Count);

        foreach (var issue in Issues)
        {
            if (issue.StartOffset + issue.Length <= oldRegion.Start)
                kept.Add(issue);
            else if (issue.StartOffset >= oldRegion.End)
                kept.Add(issue with { StartOffset = issue.StartOffset + delta });
        }

        return From(kept.Concat(fresh), newVersion);
    }

    private static List<TextIssue> Sort(IEnumerable<TextIssue> issues)
    {
        var list = issues.ToList();
        list.Sort(static (a, b) => a.StartOffset != b.StartOffset
            ? a.StartOffset.CompareTo(b.StartOffset)
            : a.Length.CompareTo(b.Length));
        return list;
    }
}
