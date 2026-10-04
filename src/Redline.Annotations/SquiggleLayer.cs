using System.Windows;
using System.Windows.Media;
using Redline.Core.Geometry;
using Redline.Core.Models;

namespace Redline.Annotations;

/// <summary>
/// Draws squiggles from overlay-local physical-pixel rectangles. Rendering is pushed through a
/// 1/scale transform so geometry stays in physical pixels end to end and WPF's DPI scaling maps it
/// back exactly — no DIP rounding between UIA's rectangles and the pixels on screen.
/// </summary>
internal sealed class SquiggleLayer : FrameworkElement
{
    private static readonly Dictionary<IssueCategory, Brush> Brushes = new()
    {
        [IssueCategory.Spelling] = Frozen(Color.FromRgb(0xE0, 0x24, 0x1B)),
        [IssueCategory.Grammar] = Frozen(Color.FromRgb(0x1F, 0x6F, 0xE0)),
        [IssueCategory.Style] = Frozen(Color.FromRgb(0xC8, 0x8A, 0x00)),
        [IssueCategory.Punctuation] = Frozen(Color.FromRgb(0x8A, 0x4F, 0xC8)),
        [IssueCategory.Other] = Frozen(Color.FromRgb(0x70, 0x70, 0x70)),
    };

    private IReadOnlyList<SquiggleSpan> _spans = Array.Empty<SquiggleSpan>();
    private double _scale = 1.0;

    public SquiggleLayer()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    public void Update(IReadOnlyList<SquiggleSpan> spans, double scale)
    {
        _spans = spans;
        _scale = scale;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_spans.Count == 0) return;
        dc.PushTransform(new ScaleTransform(1 / _scale, 1 / _scale));

        foreach (var group in _spans.GroupBy(s => s.Category))
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                foreach (var span in group)
                {
                    var points = OverlayLayout.Squiggle(span.Rect, _scale);
                    ctx.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: false, isClosed: false);
                    ctx.PolyLineTo(points.Skip(1).Select(p => new Point(p.X, p.Y)).ToList(), isStroked: true, isSmoothJoin: true);
                }
            }
            geometry.Freeze();
            var pen = new Pen(Brushes.GetValueOrDefault(group.Key, Brushes[IssueCategory.Other]), 1.25 * _scale)
            {
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        dc.Pop();
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
