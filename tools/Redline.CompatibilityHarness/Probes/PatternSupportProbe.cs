using System.Diagnostics;
using System.Windows.Automation;

namespace Redline.CompatibilityHarness.Probes;

public static class PatternSupportProbe
{
    private const int UIA_TextPattern2Id = 10024;
    private const int UIA_TextEditPatternId = 10032;

    public static PatternSupportResult Run(AutomationElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var current = element.Current;

        string processName = "Unknown";
        try
        {
            if (current.ProcessId > 0)
            {
                using var proc = Process.GetProcessById(current.ProcessId);
                processName = proc.ProcessName;
            }
        }
        catch
        {
            // Process may have exited or permissions denied
        }

        var targetInfo = new TargetAppInfo
        {
            ProcessName = processName,
            ProcessId = current.ProcessId,
            WindowTitle = current.Name ?? string.Empty,
            ControlType = current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty),
            ClassName = current.ClassName ?? string.Empty,
            FrameworkId = current.FrameworkId ?? string.Empty,
            AutomationId = current.AutomationId ?? string.Empty,
            NativeWindowHandle = (long)current.NativeWindowHandle
        };

        var supportedPatterns = new List<string>();
        foreach (var pattern in element.GetSupportedPatterns())
        {
            supportedPatterns.Add(pattern.ProgrammaticName.Replace("PatternIdentifiers.Pattern", string.Empty));
        }

        bool supportsText = element.TryGetCurrentPattern(TextPattern.Pattern, out _);
        bool supportsValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out _);
        bool supportsScroll = element.TryGetCurrentPattern(ScrollPattern.Pattern, out _);

        // Check TextPattern2 and TextEditPattern via pattern ID lookup
        bool supportsText2 = false;
        try
        {
            var text2Pattern = AutomationPattern.LookupById(UIA_TextPattern2Id);
            if (text2Pattern != null && element.TryGetCurrentPattern(text2Pattern, out _))
            {
                supportsText2 = true;
                if (!supportedPatterns.Contains("TextPattern2"))
                    supportedPatterns.Add("TextPattern2");
            }
        }
        catch
        {
            // Ignored if not supported on OS/element
        }

        bool supportsTextEdit = false;
        try
        {
            var textEditPattern = AutomationPattern.LookupById(UIA_TextEditPatternId);
            if (textEditPattern != null && element.TryGetCurrentPattern(textEditPattern, out _))
            {
                supportsTextEdit = true;
                if (!supportedPatterns.Contains("TextEditPattern"))
                    supportedPatterns.Add("TextEditPattern");
            }
        }
        catch
        {
            // Ignored
        }

        bool isPassword = current.IsPassword;
        bool isReadOnly = false;
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valObj) && valObj is ValuePattern vp)
        {
            isReadOnly = vp.Current.IsReadOnly;
        }

        return new PatternSupportResult
        {
            TargetInfo = targetInfo,
            SupportsTextPattern = supportsText,
            SupportsTextPattern2 = supportsText2,
            SupportsValuePattern = supportsValue,
            SupportsScrollPattern = supportsScroll,
            SupportsTextEditPattern = supportsTextEdit,
            IsPassword = isPassword,
            IsReadOnly = isReadOnly,
            AllSupportedPatterns = supportedPatterns
        };
    }
}
