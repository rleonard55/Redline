using Redline.Core.Models;

namespace Redline.CompatibilityHarness.Probes;

public record TargetAppInfo
{
    public string ProcessName { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public string WindowTitle { get; init; } = string.Empty;
    public string ControlType { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string FrameworkId { get; init; } = string.Empty;
    public string AutomationId { get; init; } = string.Empty;
    public long NativeWindowHandle { get; init; }
}

public record PatternSupportResult
{
    public required TargetAppInfo TargetInfo { get; init; }
    public bool SupportsTextPattern { get; init; }
    public bool SupportsTextPattern2 { get; init; }
    public bool SupportsValuePattern { get; init; }
    public bool SupportsScrollPattern { get; init; }
    public bool SupportsTextEditPattern { get; init; }
    public bool IsPassword { get; init; }
    public bool IsReadOnly { get; init; }
    public IReadOnlyList<string> AllSupportedPatterns { get; init; } = Array.Empty<string>();
}

public record TextReadResult
{
    public bool Success { get; init; }
    public string Method { get; init; } = "None"; // TextPattern | ValuePattern | NameProperty | Failed
    public string TextPreview { get; init; } = string.Empty;
    public string FullText { get; init; } = string.Empty;
    public int TextLength => FullText.Length;
    public double DurationMs { get; init; }
    public string? Error { get; init; }
}

public record CaretSelectionResult
{
    public bool CanGetCaretRange { get; init; }
    public int? CaretOffset { get; init; }
    public bool CanGetSelection { get; init; }
    public string? SelectionText { get; init; }
    public int SelectionRangeCount { get; init; }
    public string? Error { get; init; }
}

public record GeometryResult
{
    public bool CanGetBoundingRectangles { get; init; }
    public string TestedRange { get; init; } = string.Empty;
    public IReadOnlyList<TextBounds> BoundingRects { get; init; } = Array.Empty<TextBounds>();
    public bool AreRectsReasonable { get; init; }
    public double RectComputeDurationMs { get; init; }
    public bool? VisuallyVerified { get; set; }
    public string? Error { get; init; }
}

public record ChangeDetectionResult
{
    public bool ReceivedTextChangedEvent { get; init; }
    public int TextChangedEventCount { get; init; }
    public double? TextChangedLatencyMs { get; init; }
    public bool ReceivedSelectionChangedEvent { get; init; }
    public bool ReceivedValueChangedEvent { get; init; }
    public string? Error { get; init; }
}
