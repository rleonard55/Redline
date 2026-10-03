using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Redline.CompatibilityHarness.Probes;

public static class CaretSelectionProbe
{
    public static CaretSelectionResult Run(AutomationElement element, PatternSupportResult patterns)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (!patterns.SupportsTextPattern || !element.TryGetCurrentPattern(TextPattern.Pattern, out var tpObj) || tpObj is not TextPattern tp)
        {
            return new CaretSelectionResult
            {
                CanGetCaretRange = false,
                CanGetSelection = false,
                Error = "TextPattern is not supported on this element."
            };
        }

        try
        {
            var selectionRanges = tp.GetSelection();
            if (selectionRanges == null || selectionRanges.Length == 0)
            {
                return new CaretSelectionResult
                {
                    CanGetCaretRange = false,
                    CanGetSelection = false,
                    SelectionRangeCount = 0,
                    Error = "GetSelection returned no ranges."
                };
            }

            var primaryRange = selectionRanges[0];
            var selectionText = primaryRange.GetText(-1);

            // Compute offset relative to document start:
            // Clone document range start, and find distance to selection start
            int? caretOffset = null;
            try
            {
                var docRange = tp.DocumentRange;
                var testRange = docRange.Clone();
                testRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, primaryRange, TextPatternRangeEndpoint.Start);
                var prefixText = testRange.GetText(-1);
                caretOffset = prefixText?.Length;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to compute caret offset: {ex.Message}");
            }

            bool isCollapsed = string.IsNullOrEmpty(selectionText);

            return new CaretSelectionResult
            {
                CanGetCaretRange = isCollapsed,
                CaretOffset = caretOffset,
                CanGetSelection = true,
                SelectionText = isCollapsed ? null : selectionText,
                SelectionRangeCount = selectionRanges.Length
            };
        }
        catch (Exception ex)
        {
            return new CaretSelectionResult
            {
                CanGetCaretRange = false,
                CanGetSelection = false,
                Error = $"Failed querying selection/caret: {ex.Message}"
            };
        }
    }
}
