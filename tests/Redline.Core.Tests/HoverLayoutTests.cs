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

public class GutterLayoutTests
{
    private static readonly TextBounds Surface = new(100, 100, 600, 400);

    [Fact]
    public void Place_SpansTheParagraphsLines_JustLeftOfTheText()
    {
        TextBounds[] lines = [new(110, 120, 500, 20), new(110, 140, 300, 20)];

        var rect = GutterLayout.Place(lines, Surface, scale: 1.0)!.Value;

        Assert.Equal(120, rect.Top);
        Assert.Equal(160, rect.Bottom);
        Assert.Equal(GutterLayout.HitWidth, rect.Width);
        // The bar (centre of the strip) sits Gap + half a bar left of the text.
        Assert.Equal(110 - GutterLayout.Gap - GutterLayout.BarWidth / 2, rect.Left + rect.Width / 2);
    }

    [Fact]
    public void Place_ClipsToTheVisibleSurface_AndKeepsTheBarInsideIt()
    {
        TextBounds[] lines = [new(100, 60, 500, 20), new(100, 80, 500, 20), new(100, 100, 500, 20), new(100, 120, 500, 20)];

        var rect = GutterLayout.Place(lines, Surface, scale: 1.0)!.Value;

        Assert.Equal(100, rect.Top); // lines scrolled above the surface don't count
        Assert.Equal(140, rect.Bottom);
        // Text at the very edge: the bar sits flush inside the surface's left edge.
        Assert.Equal(100 + GutterLayout.BarWidth / 2, rect.Left + rect.Width / 2);
    }

    [Fact]
    public void Place_ShortParagraphsGetAMinimumHeight_ScaledForDpi()
    {
        var rect = GutterLayout.Place([new TextBounds(200, 200, 40, 8)], Surface, scale: 1.5)!.Value;

        Assert.Equal(GutterLayout.MinHeight * 1.5, rect.Height);
        Assert.Equal(GutterLayout.HitWidth * 1.5, rect.Width);
        Assert.InRange(rect.Top + rect.Height / 2, 203, 205); // centred on the line (whole pixels)
    }

    [Fact]
    public void Place_IsNullWhenNothingIsVisible()
    {
        Assert.Null(GutterLayout.Place([], Surface, 1.0));
        Assert.Null(GutterLayout.Place([TextBounds.Empty], Surface, 1.0));
        Assert.Null(GutterLayout.Place([new TextBounds(110, 600, 100, 20)], Surface, 1.0));
    }

    [Fact]
    public void Extent_IsTheUnionOfVisibleLines()
    {
        TextBounds[] lines = [new(110, 90, 500, 20), new(130, 110, 200, 20)];

        Assert.Equal(new TextBounds(110, 100, 500, 30), GutterLayout.Extent(lines, Surface));
    }
}
