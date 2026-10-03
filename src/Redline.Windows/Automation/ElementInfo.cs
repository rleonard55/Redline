using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Automation;

namespace Redline.Windows.Automation;

/// <summary>
/// Plain-data capture of an element's properties, taken once on the UIA thread so that
/// filters and selectors can inspect it without further cross-process calls.
/// <see cref="Name"/> may contain user content for some controls — never log it.
/// </summary>
public sealed record ElementInfo
{
    public required string RuntimeId { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string ControlType { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string FrameworkId { get; init; } = string.Empty;
    public string AutomationId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public long NativeWindowHandle { get; init; }
    public bool IsPassword { get; init; }
    public bool IsEnabled { get; init; }
    public bool SupportsTextPattern { get; init; }
    public bool SupportsValuePattern { get; init; }
    public bool ValueIsReadOnly { get; init; }

    public string SurfaceId => $"{ProcessId}:{RuntimeId}";

    private static readonly ConcurrentDictionary<int, string> ProcessNames = new();

    /// <summary>Must be called on the UIA dispatcher thread. Throws <see cref="ElementNotAvailableException"/> if the element is gone.</summary>
    public static ElementInfo Capture(AutomationElement element)
    {
        var current = element.Current;

        bool supportsValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj);
        bool valueReadOnly = supportsValue && vpObj is ValuePattern vp && vp.Current.IsReadOnly;

        return new ElementInfo
        {
            RuntimeId = string.Join(".", element.GetRuntimeId() ?? Array.Empty<int>()),
            ProcessId = current.ProcessId,
            ProcessName = GetProcessName(current.ProcessId),
            ControlType = current.ControlType?.ProgrammaticName.Replace("ControlType.", string.Empty) ?? string.Empty,
            ClassName = current.ClassName ?? string.Empty,
            FrameworkId = current.FrameworkId ?? string.Empty,
            AutomationId = current.AutomationId ?? string.Empty,
            Name = current.Name ?? string.Empty,
            NativeWindowHandle = current.NativeWindowHandle,
            IsPassword = current.IsPassword,
            IsEnabled = current.IsEnabled,
            SupportsTextPattern = element.TryGetCurrentPattern(TextPattern.Pattern, out _),
            SupportsValuePattern = supportsValue,
            ValueIsReadOnly = valueReadOnly,
        };
    }

    private static string GetProcessName(int pid)
    {
        if (pid <= 0) return "Unknown";
        if (ProcessNames.TryGetValue(pid, out var cached)) return cached;

        string name;
        try
        {
            using var proc = Process.GetProcessById(pid);
            name = proc.ProcessName + ".exe";
        }
        catch
        {
            return "Unknown"; // don't cache: may be a transient access failure
        }

        if (ProcessNames.Count > 512) ProcessNames.Clear(); // PIDs get reused; keep this bounded
        ProcessNames[pid] = name;
        return name;
    }
}
