using Redline.Core.Models;

namespace Redline.Core.Geometry;

/// <summary>One squiggle to draw: a line rectangle in overlay-local physical pixels.</summary>
public readonly record struct SquiggleSpan(TextBounds Rect, IssueCategory Category);

/// <summary>
/// Pure geometry for the annotation overlay. All inputs are physical screen pixels; outputs are
/// relative to the overlay's top-left corner.
/// </summary>
public static class OverlayLayout
{
    /// <summary>
    /// Clips each issue's line rectangles to the visible <paramref name="overlay"/> area and makes
    /// them overlay-local. Rectangles outside it (text scrolled out of view) are dropped; a rectangle
    /// cut off at the bottom keeps its squiggle only if the text's baseline is still visible.
    /// </summary>
    public static IReadOnlyList<SquiggleSpan> Layout(TextBounds overlay, IEnumerable<(IssueCategory Category, IReadOnlyList<TextBounds> Rects)> issues)
    {
        var spans = new List<SquiggleSpan>();
        foreach (var (category, rects) in issues)
        {
            foreach (var r in rects)
            {
                if (r.IsEmpty || r.Width < 2) continue;

                // The squiggle sits at the bottom of the line box, so that edge must be visible.
                if (r.Bottom > overlay.Bottom || r.Bottom - 2 < overlay.Top) continue;

                double left = Math.Max(r.Left, overlay.Left);
                double right = Math.Min(r.Right, overlay.Right);
                if (right - left < 2) continue;

                double top = Math.Max(r.Top, overlay.Top);
                spans.Add(new SquiggleSpan(
                    new TextBounds(left - overlay.Left, top - overlay.Top, right - left, r.Bottom - top),
                    category));
            }
        }
        return spans;
    }

    /// <summary>
    /// Zig-zag points for a squiggle along the bottom of <paramref name="rect"/>, sized for
    /// <paramref name="scale"/> (physical pixels per DIP, e.g. 1.5 at 150%).
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Squiggle(TextBounds rect, double scale)
    {
        double halfPeriod = 2.0 * scale;
        double amplitude = 1.25 * scale;
        double baseline = rect.Bottom - amplitude - 0.5 * scale;

        var points = new List<(double, double)>();
        bool up = true;
        for (double x = rect.Left; x < rect.Right; x += halfPeriod)
        {
            points.Add((x, baseline + (up ? -amplitude : amplitude)));
            up = !up;
        }
        points.Add((rect.Right, baseline + (up ? -amplitude : amplitude)));
        return points;
    }
}
