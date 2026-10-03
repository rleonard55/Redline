using Redline.Core.Models;
using Xunit;

namespace Redline.Core.Tests;

public class ModelTests
{
    [Fact]
    public void TextRange_EndAndContainment_BehaveCorrectly()
    {
        var range = new TextRange(10, 5);
        Assert.Equal(15, range.End);
        Assert.False(range.IsEmpty);
        Assert.True(range.Contains(10));
        Assert.True(range.Contains(14));
        Assert.False(range.Contains(15));
        Assert.False(range.Contains(9));
    }

    [Fact]
    public void TextRange_Intersection_DetectsOverlap()
    {
        var r1 = new TextRange(5, 10); // [5..15)
        var r2 = new TextRange(12, 5); // [12..17)
        var r3 = new TextRange(15, 5); // [15..20)

        Assert.True(r1.IntersectsWith(r2));
        Assert.False(r1.IntersectsWith(r3));
    }

    [Fact]
    public void TextBounds_CoordinatesAndContainment_BehaveCorrectly()
    {
        var bounds = new TextBounds(100, 200, 50, 20);
        Assert.Equal(100, bounds.Left);
        Assert.Equal(200, bounds.Top);
        Assert.Equal(150, bounds.Right);
        Assert.Equal(220, bounds.Bottom);
        Assert.False(bounds.IsEmpty);
        Assert.True(bounds.Contains(125, 210));
        Assert.False(bounds.Contains(90, 210));
    }

    [Fact]
    public void TextSnapshot_Substring_ExtractsSafely()
    {
        var snapshot = new TextSnapshot("Hello world of Redline", 1, DateTimeOffset.UtcNow);
        Assert.Equal(22, snapshot.Length);

        var sub = snapshot.GetSubstring(new TextRange(6, 5));
        Assert.Equal("world", sub);

        // Edge case: range beyond bounds
        var clamped = snapshot.GetSubstring(new TextRange(15, 100));
        Assert.Equal("Redline", clamped);
    }
}
