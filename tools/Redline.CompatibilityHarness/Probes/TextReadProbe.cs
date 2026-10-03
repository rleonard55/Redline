using System.Diagnostics;
using System.Windows.Automation;

namespace Redline.CompatibilityHarness.Probes;

public static class TextReadProbe
{
    public static TextReadResult Run(AutomationElement element, PatternSupportResult patterns)
    {
        ArgumentNullException.ThrowIfNull(element);
        var sw = Stopwatch.StartNew();

        // 1. Preferred: TextPattern
        if (patterns.SupportsTextPattern && element.TryGetCurrentPattern(TextPattern.Pattern, out var tpObj) && tpObj is TextPattern tp)
        {
            try
            {
                var docRange = tp.DocumentRange;
                var text = docRange.GetText(-1) ?? string.Empty;
                sw.Stop();

                return new TextReadResult
                {
                    Success = true,
                    Method = "TextPattern",
                    FullText = text,
                    TextPreview = text.Length > 500 ? text[..500] + "..." : text,
                    DurationMs = sw.Elapsed.TotalMilliseconds
                };
            }
            catch (Exception ex)
            {
                // Fall through to next method
                Debug.WriteLine($"TextPattern read failed: {ex.Message}");
            }
        }

        // 2. ValuePattern
        if (patterns.SupportsValuePattern && element.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) && vpObj is ValuePattern vp)
        {
            try
            {
                var val = vp.Current.Value ?? string.Empty;
                sw.Stop();

                return new TextReadResult
                {
                    Success = true,
                    Method = "ValuePattern",
                    FullText = val,
                    TextPreview = val.Length > 500 ? val[..500] + "..." : val,
                    DurationMs = sw.Elapsed.TotalMilliseconds
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ValuePattern read failed: {ex.Message}");
            }
        }

        // 3. Name property fallback
        try
        {
            var name = element.Current.Name;
            if (!string.IsNullOrEmpty(name))
            {
                sw.Stop();
                return new TextReadResult
                {
                    Success = true,
                    Method = "NameProperty",
                    FullText = name,
                    TextPreview = name.Length > 500 ? name[..500] + "..." : name,
                    DurationMs = sw.Elapsed.TotalMilliseconds
                };
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TextReadResult
            {
                Success = false,
                Method = "Failed",
                Error = $"Failed to read text via all methods: {ex.Message}",
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }

        sw.Stop();
        return new TextReadResult
        {
            Success = false,
            Method = "Failed",
            Error = "Element does not expose text via TextPattern, ValuePattern, or Name property.",
            DurationMs = sw.Elapsed.TotalMilliseconds
        };
    }
}
