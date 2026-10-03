namespace Redline.Core.Models;

/// <summary>
/// Identifies the editable surface currently being tracked (process, window, control).
/// Contains no user text, so it is safe to log.
/// </summary>
public record TextSurfaceContext
{
    /// <summary>Stable identity for the control while it lives (process id + UIA runtime id).</summary>
    public required string SurfaceId { get; init; }
    public required string ProcessName { get; init; }
    public int ProcessId { get; init; }
    public string WindowTitle { get; init; } = string.Empty;
    public string ControlType { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string FrameworkId { get; init; } = string.Empty;
    public long NativeWindowHandle { get; init; }
    public string AdapterName { get; init; } = string.Empty;

    public override string ToString() => $"{ProcessName} [{ControlType}/{ClassName}] via {AdapterName}";
}
