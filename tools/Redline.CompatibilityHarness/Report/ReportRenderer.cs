using System.Text;
using System.Text.Json;

namespace Redline.CompatibilityHarness.Report;

public static class ReportRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string ToJson(CompatibilityReport report)
    {
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static string ToMarkdown(CompatibilityReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Redline Compatibility Harness Report");
        sb.AppendLine();
        sb.AppendLine($"> Generated: {report.Timestamp:yyyy-MM-dd HH:mm:ss UTC}");
        sb.AppendLine($"> OS: {report.Machine.OSVersion} | CLR: {report.Machine.ClrVersion} | DPI Scale: {report.Machine.PrimaryDpiScale:P0}");
        sb.AppendLine();

        sb.AppendLine("## Summary Matrix");
        sb.AppendLine();
        sb.AppendLine("| Application | Process | Read | Caret | Geometry | Change Event | Replace |");
        sb.AppendLine("|---|---|:---:|:---:|:---:|:---:|:---:|");

        foreach (var r in report.Results)
        {
            string read = r.TextRead.Success ? "✅" : "❌";
            string caret = r.CaretSelection != null ? (r.CaretSelection.CanGetCaretRange ? "✅" : "⚠️") : "—";
            string geom = r.Geometry != null ? (r.Geometry.CanGetBoundingRectangles ? (r.Geometry.AreRectsReasonable ? "✅" : "⚠️") : "❌") : "—";
            string change = r.ChangeDetection != null ? (r.ChangeDetection.ReceivedTextChangedEvent || r.ChangeDetection.ReceivedValueChangedEvent ? "✅" : "❌") : "—";
            string replace = r.Replace != null ? (r.Replace.Success ? "✅" : "❌") : "—";

            sb.AppendLine($"| {r.AppName} | `{r.TargetInfo.ProcessName}` | {read} | {caret} | {geom} | {change} | {replace} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Detailed Probe Findings");
        sb.AppendLine();

        foreach (var r in report.Results)
        {
            sb.AppendLine($"### {r.AppName} (`{r.TargetInfo.ProcessName}`)");
            sb.AppendLine();
            sb.AppendLine($"- **Control Type:** `{r.TargetInfo.ControlType}`");
            sb.AppendLine($"- **Class Name:** `{r.TargetInfo.ClassName}`");
            sb.AppendLine($"- **Framework ID:** `{r.TargetInfo.FrameworkId}`");
            sb.AppendLine($"- **Supported Patterns:** {string.Join(", ", r.PatternSupport.AllSupportedPatterns)}");
            sb.AppendLine();

            sb.AppendLine("#### 1. Text Reading");
            sb.AppendLine($"- **Success:** {r.TextRead.Success}");
            sb.AppendLine($"- **Method:** `{r.TextRead.Method}`");
            sb.AppendLine($"- **Duration:** {r.TextRead.DurationMs:F1} ms");
            sb.AppendLine($"- **Preview:** `{r.TextRead.TextPreview.Replace("\r", "").Replace("\n", " ")}`");
            if (!string.IsNullOrEmpty(r.TextRead.Error)) sb.AppendLine($"- **Error:** {r.TextRead.Error}");
            sb.AppendLine();

            if (r.CaretSelection != null)
            {
                sb.AppendLine("#### 2. Caret / Selection");
                sb.AppendLine($"- **Can Get Caret:** {r.CaretSelection.CanGetCaretRange}");
                sb.AppendLine($"- **Caret Offset:** {r.CaretSelection.CaretOffset?.ToString() ?? "N/A"}");
                sb.AppendLine($"- **Can Get Selection:** {r.CaretSelection.CanGetSelection}");
                sb.AppendLine($"- **Selection Text:** `{r.CaretSelection.SelectionText ?? "(none)"}`");
                if (!string.IsNullOrEmpty(r.CaretSelection.Error)) sb.AppendLine($"- **Error:** {r.CaretSelection.Error}");
                sb.AppendLine();
            }

            if (r.Geometry != null)
            {
                sb.AppendLine("#### 3. Geometry Mapping");
                sb.AppendLine($"- **Can Get Rects:** {r.Geometry.CanGetBoundingRectangles}");
                sb.AppendLine($"- **Tested Range:** `{r.Geometry.TestedRange}`");
                sb.AppendLine($"- **Rects Returned:** {r.Geometry.BoundingRects.Count}");
                sb.AppendLine($"- **Are Rects Reasonable:** {r.Geometry.AreRectsReasonable}");
                if (r.Geometry.VisuallyVerified.HasValue) sb.AppendLine($"- **Visually Verified:** {r.Geometry.VisuallyVerified.Value}");
                sb.AppendLine($"- **Duration:** {r.Geometry.RectComputeDurationMs:F1} ms");
                if (!string.IsNullOrEmpty(r.Geometry.Error)) sb.AppendLine($"- **Error:** {r.Geometry.Error}");
                sb.AppendLine();
            }

            if (r.ChangeDetection != null)
            {
                sb.AppendLine("#### 4. Change Detection");
                sb.AppendLine($"- **TextChangedEvent:** {r.ChangeDetection.ReceivedTextChangedEvent} ({r.ChangeDetection.TextChangedEventCount}x)");
                sb.AppendLine($"- **ValueChangedEvent:** {r.ChangeDetection.ReceivedValueChangedEvent}");
                sb.AppendLine($"- **SelectionChangedEvent:** {r.ChangeDetection.ReceivedSelectionChangedEvent}");
                if (r.ChangeDetection.TextChangedLatencyMs.HasValue) sb.AppendLine($"- **First Event Latency:** {r.ChangeDetection.TextChangedLatencyMs.Value:F0} ms");
                if (!string.IsNullOrEmpty(r.ChangeDetection.Error)) sb.AppendLine($"- **Error:** {r.ChangeDetection.Error}");
                sb.AppendLine();
            }

            if (r.Replace != null)
            {
                sb.AppendLine("#### 5. Text Replacement");
                sb.AppendLine($"- **Success:** {r.Replace.Success}");
                sb.AppendLine($"- **Method:** `{r.Replace.Method}`");
                sb.AppendLine($"- **Verified Correct:** {r.Replace.VerifiedCorrect}");
                sb.AppendLine($"- **Duration:** {r.Replace.DurationMs:F1} ms");
                if (!string.IsNullOrEmpty(r.Replace.Error)) sb.AppendLine($"- **Error:** {r.Replace.Error}");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
