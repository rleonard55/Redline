using Redline.Core.Geometry;
using Redline.Core.Models;
using Xunit;

namespace Redline.Core.Tests;

public class OverlayLayoutTests
{
    private static readonly TextBounds Overlay = new(100, 200, 400, 300); // x 100..500, y 200..500

    private static IReadOnlyList<SquiggleSpan> Layout(params TextBounds[] rects) =>
        OverlayLayout.Layout(Overlay, [(IssueCategory.Spelling, rects)]);

    [Fact]
    public void VisibleRect_BecomesOverlayLocal()
    {
        var span = Assert.Single(Layout(new TextBounds(150, 250, 40, 20)));
        Assert.Equal(new TextBounds(50, 50, 40, 20), span.Rect);
        Assert.Equal(IssueCategory.Spelling, span.Category);
    }

    [Fact]
    public void RectsScrolledOutOfView_AreDropped()
    {
        Assert.Empty(Layout(new TextBounds(150, 100, 40, 20)));   // above
        Assert.Empty(Layout(new TextBounds(150, 600, 40, 20)));   // below
        Assert.Empty(Layout(new TextBounds(10, 250, 40, 20)));    // left
        Assert.Empty(Layout(new TextBounds(150, 490, 40, 20)));   // baseline cut off at the bottom
    }

    [Fact]
    public void PartiallyVisibleRect_IsClippedHorizontally()
    {
        var span = Assert.Single(Layout(new TextBounds(480, 250, 60, 20)));
        Assert.Equal(new TextBounds(380, 50, 20, 20), span.Rect);
    }

    [Fact]
    public void MultiLineIssue_ProducesOneSpanPerVisibleLine()
    {
        Assert.Equal(2, Layout(new TextBounds(400, 250, 90, 20), new TextBounds(110, 272, 60, 20)).Count);
    }

    [Fact]
    public void EmptyAndHairlineRects_AreIgnored()
    {
        Assert.Empty(Layout(new TextBounds(150, 250, 0, 20), new TextBounds(150, 250, 1, 20)));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Squiggle_StaysInsideTheRectBottomAndSpansItsWidth(double scale)
    {
        var rect = new TextBounds(10, 20, 37, 18);
        var points = OverlayLayout.Squiggle(rect, scale);

        Assert.Equal(rect.Left, points[0].X);
        Assert.Equal(rect.Right, points[^1].X);
        Assert.All(points, p => Assert.InRange(p.Y, rect.Bottom - 3 * scale - 0.01, rect.Bottom));
        Assert.True(points.Count >= 37 / (2 * scale));
    }
}
