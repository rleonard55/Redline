using Redline.Core.Models;
using Redline.Core.Pipeline;
using Redline.Core.Text;
using Xunit;

namespace Redline.Core.Tests;

public class TextDiffTests
{
    [Theory]
    [InlineData("hello world", "hello brave world", 6, 0, 6)]   // insertion
    [InlineData("hello brave world", "hello world", 6, 6, 0)]   // deletion
    [InlineData("the cat sat", "the dog sat", 4, 3, 3)]         // replacement
    [InlineData("", "abc", 0, 0, 3)]                            // from empty
    [InlineData("abc", "", 0, 3, 0)]                            // to empty
    [InlineData("same", "same", 4, 0, 0)]                       // no change
    public void Compute_FindsMinimalContiguousChange(string oldText, string newText, int start, int oldLen, int newLen)
    {
        Assert.Equal(new TextChange(start, oldLen, newLen), TextDiff.Compute(oldText, newText));
    }

    [Fact]
    public void Compute_RepeatedCharacters_DoesNotOverlapPrefixAndSuffix()
    {
        // "aaa" -> "aaaa": prefix and suffix could both claim the middle 'a's.
        var change = TextDiff.Compute("aaa", "aaaa");
        Assert.Equal(1, change.NewLength - change.OldLength);
        Assert.True(change.Start + change.OldLength <= 3);
        Assert.True(change.Start + change.NewLength <= 4);
    }

    [Fact]
    public void Compute_NeverSplitsSurrogatePair()
    {
        // Two emoji sharing a high surrogate (U+1F600 vs U+1F601).
        var oldText = "a" + char.ConvertFromUtf32(0x1F600) + "b";
        var newText = "a" + char.ConvertFromUtf32(0x1F601) + "b";
        var change = TextDiff.Compute(oldText, newText);
        Assert.Equal(new TextChange(1, 2, 2), change);
    }

    [Fact]
    public void Compute_AppliedChangeReproducesNewText()
    {
        var rng = new Random(1234);
        const string alphabet = "ab \n";
        for (int i = 0; i < 2000; i++)
        {
            var a = RandomString(rng, alphabet, rng.Next(0, 12));
            var b = RandomString(rng, alphabet, rng.Next(0, 12));
            var c = TextDiff.Compute(a, b);
            var rebuilt = a[..c.Start] + b.Substring(c.Start, c.NewLength) + a[(c.Start + c.OldLength)..];
            Assert.Equal(b, rebuilt);
        }
    }

    private static string RandomString(Random rng, string alphabet, int length) =>
        new(Enumerable.Range(0, length).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());
}

public class ContextExpanderTests
{
    private const string Text = "First line.\nSecond line here.\r\nThird.";

    [Fact]
    public void ToParagraph_ExpandsToEnclosingLine()
    {
        int idx = Text.IndexOf("line here", StringComparison.Ordinal);
        var range = ContextExpander.ToParagraph(Text, new TextRange(idx, 4));
        Assert.Equal("Second line here.", Text.Substring(range.Start, range.Length));
    }

    [Fact]
    public void ToParagraph_SpanningLines_CoversBoth()
    {
        var range = ContextExpander.ToParagraph(Text, new TextRange(3, 15));
        Assert.Equal("First line.\nSecond line here.", Text.Substring(range.Start, range.Length));
    }

    [Fact]
    public void ToParagraph_EmptyRangeAtEnd_ReturnsLastLine()
    {
        var range = ContextExpander.ToParagraph(Text, new TextRange(Text.Length, 0));
        Assert.Equal("Third.", Text.Substring(range.Start, range.Length));
    }

    [Fact]
    public void ToParagraph_OnLineBreak_ReturnsEmptyRange()
    {
        // Caret on an empty line between two line breaks.
        var range = ContextExpander.ToParagraph("a\n\nb", new TextRange(2, 0));
        Assert.Equal(new TextRange(2, 0), range);
    }
}

public class DocumentStateTests
{
    [Fact]
    public void Update_IgnoresUnchangedText_AndVersionsIncrease()
    {
        var state = new DocumentState();
        state.Reset("s1");

        var first = state.Update("s1", "hello");
        Assert.NotNull(first);
        Assert.Null(first.Value.Change);

        Assert.Null(state.Update("s1", "hello"));

        var second = state.Update("s1", "hello!");
        Assert.NotNull(second);
        Assert.True(second.Value.Snapshot.Version > first.Value.Snapshot.Version);
        Assert.Equal(new TextChange(5, 0, 1), second.Value.Change);
        Assert.Equal("hello", state.Previous!.Text);
    }

    [Fact]
    public void Update_FromStaleSurface_IsRejected()
    {
        var state = new DocumentState();
        state.Reset("s1");
        state.Update("s1", "one");
        state.Reset("s2");

        Assert.Null(state.Update("s1", "late read from old surface"));
        Assert.Null(state.Current);
    }

    [Fact]
    public void Versions_KeepIncreasingAcrossSurfaces()
    {
        var state = new DocumentState();
        state.Reset("a");
        var v1 = state.Update("a", "x")!.Value.Snapshot.Version;
        state.Reset("b");
        var v2 = state.Update("b", "x")!.Value.Snapshot.Version;
        Assert.True(v2 > v1);
    }
}

public class IssueSetTests
{
    private static TextIssue Issue(int start, int length, string tag = "") => new()
    {
        StartOffset = start,
        Length = length,
        OriginalText = tag,
        Category = IssueCategory.Spelling,
        Message = tag,
    };

    [Fact]
    public void Rebase_KeepsBefore_ShiftsAfter_DropsOverlapping()
    {
        // Old text regions: [0,5) before, [10,20) edited region, [25,30) after.
        var set = IssueSet.From([Issue(0, 5, "before"), Issue(12, 3, "inside"), Issue(18, 4, "straddle"), Issue(25, 5, "after")], 1);

        // Region [10,20) replaced by 13 chars (+3), with one fresh issue at 11.
        var rebased = set.Rebase(new TextRange(10, 10), 13, [Issue(11, 2, "fresh")], 2);

        Assert.Equal(["before", "fresh", "after"], rebased.Issues.Select(i => i.OriginalText));
        Assert.Equal(28, rebased.Issues.Single(i => i.OriginalText == "after").StartOffset);
        Assert.All(rebased.Issues, i => Assert.Equal(2, i.SnapshotVersion));
    }

    [Fact]
    public void Rebase_IssuesTouchingRegionBoundaries_AreKept()
    {
        var set = IssueSet.From([Issue(5, 5, "endsAtStart"), Issue(20, 2, "startsAtEnd")], 1);
        var rebased = set.Rebase(new TextRange(10, 10), 10, [], 2);
        Assert.Equal(2, rebased.Issues.Count);
    }
}
