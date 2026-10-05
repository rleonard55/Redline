using Redline.Core.Models;

namespace Redline.Core.Geometry;

/// <summary>
/// Pure geometry for the paragraph gutter pill: a thin vertical bar just left of a paragraph, as tall as
/// its visible lines. Inputs and results are physical screen pixels; sizes are DIPs times <c>scale</c>.
/// </summary>
public static class GutterLayout
{
    /// <summary>The visible bar.</summary>
    public const double BarWidth = 4;

    /// <summary>The clickable strip around the bar (wider than the bar so it's easy to hit).</summary>
    public const double HitWidth = 12;

    /// <summary>Gap between the bar and the text.</summary>
    public const double Gap = 6;

    /// <summary>A one-word paragraph still gets a bar this tall.</summary>
    public const double MinHeight = 14;

    /// <summary>
    /// The pill's window rectangle (the clickable strip) for a paragraph whose lines measure
    /// <paramref name="lines"/>, clipped to the visible <paramref name="surface"/>; null when no line is visible.
    /// The bar stays inside the surface: when the text starts at its very edge, the bar sits flush with that edge
    /// rather than outside the window (the transparent strip around it may still reach a few pixels past it).
    /// </summary>
    public static TextBounds? Place(IReadOnlyList<TextBounds> lines, TextBounds surface, double scale)
    {
        var visible = lines.Where(r => !r.IsEmpty && r.Bottom > surface.Top && r.Top < surface.Bottom).ToList();
        if (visible.Count == 0) return null;

        double top = Math.Max(visible.Min(r => r.Top), surface.Top);
        double bottom = Math.Min(visible.Max(r => r.Bottom), surface.Bottom);
        double minHeight = MinHeight * scale;
        if (bottom - top < minHeight)
        {
            double middle = (top + bottom) / 2;
            (top, bottom) = (middle - minHeight / 2, middle + minHeight / 2);
        }

        double hit = HitWidth * scale;
        double barCenter = Math.Max(visible.Min(r => r.Left) - (Gap + BarWidth / 2) * scale, surface.Left + BarWidth / 2 * scale);
        return new TextBounds(Math.Round(barCenter - hit / 2), Math.Round(top), Math.Round(hit), Math.Round(bottom - top));
    }

    /// <summary>The paragraph's visible extent (union of its line rectangles, clipped to the surface), for placing the fix popup.</summary>
    public static TextBounds? Extent(IReadOnlyList<TextBounds> lines, TextBounds surface)
    {
        var visible = lines.Where(r => !r.IsEmpty && r.Bottom > surface.Top && r.Top < surface.Bottom).ToList();
        if (visible.Count == 0) return null;
        double left = visible.Min(r => r.Left), right = visible.Max(r => r.Right);
        double top = Math.Max(visible.Min(r => r.Top), surface.Top), bottom = Math.Min(visible.Max(r => r.Bottom), surface.Bottom);
        return new TextBounds(left, top, right - left, bottom - top);
    }
}
