using Redline.Core.Geometry;
using Redline.Core.Models;
using Xunit;

namespace Redline.Core.Tests;

public class HoverLayoutTests
{
    private static readonly TextBounds Work = new(0, 0, 1920, 1040); // taskbar below 1040

    [Fact]
    public void HitTest_FindsTheWordUnderThePointer()
    {
        TextBounds[] words = [new(100, 100, 40, 20), new(200, 100, 50, 20)];
        Assert.Equal(0, HoverLayout.HitTest(words, 120, 110));
        Assert.Equal(1, HoverLayout.HitTest(words, 249, 119));
        Assert.Equal(-1, HoverLayout.HitTest(words, 170, 110)); // between words
    }

    [Fact]
    public void HitTest_IncludesTheSquiggleJustBelowTheText()
    {
        TextBounds[] words = [new(100, 100, 40, 20)];
        Assert.Equal(0, HoverLayout.HitTest(words, 120, 122));
        Assert.Equal(-1, HoverLayout.HitTest(words, 120, 125));
    }

    [Fact]
    public void HitTest_OverlappingSlop_PicksTheNearerWord()
    {
        TextBounds[] words = [new(100, 100, 40, 20), new(142, 100, 40, 20)]; // 2 px apart
        Assert.Equal(0, HoverLayout.HitTest(words, 140.5, 110));
        Assert.Equal(1, HoverLayout.HitTest(words, 141.5, 110));
    }

    [Fact]
    public void HitTest_IgnoresEmptyRegions() =>
        Assert.Equal(-1, HoverLayout.HitTest([TextBounds.Empty], 0, 0));

    [Fact]
    public void Pill_GoesBelowTheWord_LeftAligned()
    {
        var (x, y) = HoverLayout.PlacePill(new TextBounds(300, 400, 40, 20), 120, 26, Work);
        Assert.Equal(300, x);
        Assert.Equal(422, y);
    }

    [Fact]
    public void Pill_FlipsAboveWhenNoRoomBelow()
    {
        var (_, y) = HoverLayout.PlacePill(new TextBounds(300, 1020, 40, 18), 120, 26, Work);
        Assert.Equal(1020 - 2 - 26, y);
    }

    [Fact]
    public void Pill_StaysOnTheMonitorHorizontally()
    {
        var (x, _) = HoverLayout.PlacePill(new TextBounds(1880, 400, 30, 20), 120, 26, Work);
        Assert.Equal(1800, x);
    }

    [Fact]
    public void KeepOpenZone_CoversWordPillAndTheGap()
    {
        var word = new TextBounds(300, 400, 40, 20);
        var pill = new TextBounds(300, 422, 120, 26);
        var zone = HoverLayout.KeepOpenZone(word, pill);
        Assert.True(zone.Contains(320, 421));   // the gap
        Assert.True(zone.Contains(410, 440));   // pill's far end
        Assert.False(zone.Contains(320, 460));  // well below
    }
}
