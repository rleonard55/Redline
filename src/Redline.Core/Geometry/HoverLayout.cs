using Redline.Core.Models;

namespace Redline.Core.Geometry;

/// <summary>
/// Pure geometry for the hover quick-fix pill. All values are physical screen pixels.
/// </summary>
public static class HoverLayout
{
    /// <summary>Extra pixels around a word that still count as pointing at it (the squiggle sits just below the text).</summary>
    public const double HitSlop = 3;

    /// <summary>Gap between the word and the pill.</summary>
    public const double PillGap = 2;

    /// <summary>
    /// Index of the region containing (<paramref name="x"/>, <paramref name="y"/>), or -1. When regions
    /// overlap (the slop of adjacent words), the one whose centre is nearest wins.
    /// </summary>
    public static int HitTest(IReadOnlyList<TextBounds> regions, double x, double y, double slop = HitSlop)
    {
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            if (r.IsEmpty || !Inflate(r, slop).Contains(x, y)) continue;
            double dx = x - (r.Left + r.Width / 2), dy = y - (r.Top + r.Height / 2);
            double distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Top-left for a pill of <paramref name="width"/> x <paramref name="height"/>: below the word,
    /// left-aligned with it; above the word if there's no room below; kept inside <paramref name="workArea"/>.
    /// </summary>
    public static (double X, double Y) PlacePill(TextBounds word, double width, double height, TextBounds workArea)
    {
        double y = word.Bottom + PillGap;
        if (y + height > workArea.Bottom)
            y = word.Top - PillGap - height;
        double x = Math.Clamp(word.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        return (x, y);
    }

    /// <summary>
    /// Area the pointer may wander in without the pill closing: the word, the pill, and the gap
    /// between them (the bounding box of both), plus slop.
    /// </summary>
    public static TextBounds KeepOpenZone(TextBounds word, TextBounds pill, double slop = HitSlop)
    {
        double left = Math.Min(word.Left, pill.Left), top = Math.Min(word.Top, pill.Top);
        double right = Math.Max(word.Right, pill.Right), bottom = Math.Max(word.Bottom, pill.Bottom);
        return Inflate(new TextBounds(left, top, right - left, bottom - top), slop);
    }

    private static TextBounds Inflate(TextBounds r, double by) =>
        new(r.Left - by, r.Top - by, r.Width + 2 * by, r.Height + 2 * by);
}
