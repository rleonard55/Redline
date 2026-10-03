using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Redline.Core.Models;

namespace Redline.CompatibilityHarness.Probes;

public static class GeometryProbe
{
    public static GeometryResult Run(AutomationElement element, PatternSupportResult patterns, TextReadResult readResult)
    {
        ArgumentNullException.ThrowIfNull(element);
        var sw = Stopwatch.StartNew();

        if (!patterns.SupportsTextPattern || !element.TryGetCurrentPattern(TextPattern.Pattern, out var tpObj) || tpObj is not TextPattern tp)
        {
            sw.Stop();
            return new GeometryResult
            {
                CanGetBoundingRectangles = false,
                Error = "TextPattern is required to map text to screen coordinates.",
                RectComputeDurationMs = sw.Elapsed.TotalMilliseconds
            };
        }

        try
        {
            var docRange = tp.DocumentRange;
            var fullText = readResult.FullText;
            if (string.IsNullOrEmpty(fullText))
            {
                fullText = docRange.GetText(-1) ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(fullText))
            {
                sw.Stop();
                return new GeometryResult
                {
                    CanGetBoundingRectangles = false,
                    Error = "Target document is empty. Type some text in the target control to test geometry.",
                    RectComputeDurationMs = sw.Elapsed.TotalMilliseconds
                };
            }

            // Find a target word to test (first word with length >= 3, or the first 5 chars)
            string targetWord = string.Empty;
            var words = fullText.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var w in words)
            {
                if (w.Length >= 3)
                {
                    targetWord = w;
                    break;
                }
            }

            if (string.IsNullOrEmpty(targetWord))
            {
                targetWord = fullText.Length > 5 ? fullText[..5] : fullText;
            }

            // Use FindText to get the exact range for the sample word
            TextPatternRange? targetRange = null;
            try
            {
                targetRange = docRange.FindText(targetWord, false, false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FindText failed: {ex.Message}");
            }

            // Fallback if FindText fails: use first character/word of document range
            if (targetRange == null)
            {
                targetRange = docRange.Clone();
                targetRange.ExpandToEnclosingUnit(TextUnit.Word);
                targetWord = targetRange.GetText(-1);
            }

            var rects = targetRange.GetBoundingRectangles();
            sw.Stop();

            if (rects == null || rects.Length == 0)
            {
                return new GeometryResult
                {
                    CanGetBoundingRectangles = false,
                    TestedRange = targetWord,
                    Error = "GetBoundingRectangles returned empty or null array.",
                    RectComputeDurationMs = sw.Elapsed.TotalMilliseconds
                };
            }

            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

            var boundsList = new List<TextBounds>();
            bool allReasonable = true;

            foreach (var r in rects)
            {
                var bounds = new TextBounds(r.X, r.Y, r.Width, r.Height);
                boundsList.Add(bounds);

                // Check sanity: width & height > 0, inside virtual desktop
                if (r.Width <= 0 || r.Height <= 0 ||
                    r.X < virtualLeft - 100 || r.X > virtualRight + 100 ||
                    r.Y < virtualTop - 100 || r.Y > virtualBottom + 100)
                {
                    allReasonable = false;
                }
            }

            return new GeometryResult
            {
                CanGetBoundingRectangles = true,
                TestedRange = targetWord,
                BoundingRects = boundsList,
                AreRectsReasonable = allReasonable,
                RectComputeDurationMs = sw.Elapsed.TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new GeometryResult
            {
                CanGetBoundingRectangles = false,
                Error = $"Exception querying bounding rectangles: {ex.Message}",
                RectComputeDurationMs = sw.Elapsed.TotalMilliseconds
            };
        }
    }
}
