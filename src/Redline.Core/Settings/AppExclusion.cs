namespace Redline.Core.Settings;

/// <summary>The tray's "Don't check in &lt;app&gt;": naming the app and adding it to the user's exclusions.</summary>
public static class AppExclusion
{
    /// <summary>
    /// A name people recognize: the exe's file description ("Microsoft Word", "Google Chrome") when it's a
    /// real name, otherwise the process name without ".exe" ("notepad").
    /// </summary>
    public static string DisplayName(string processName, string? fileDescription)
    {
        var description = fileDescription?.Trim() ?? string.Empty;
        if (description.Length is > 0 and <= 40 && !description.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return description;
        var name = RedlineSettings.NormalizeProcessName(processName);
        return name.Length > 4 ? name[..^4] : processName;
    }

    /// <summary><paramref name="settings"/> with <paramref name="processName"/> excluded (no-op if it already is).</summary>
    public static RedlineSettings Exclude(RedlineSettings settings, string processName) => settings with
    {
        Applications = settings.Applications with
        {
            Excluded = settings.Applications.Excluded.Append(RedlineSettings.NormalizeProcessName(processName)).ToList(),
        },
    };

    public static bool IsExcluded(RedlineSettings settings, string processName) =>
        settings.Applications.Excluded.Contains(RedlineSettings.NormalizeProcessName(processName), StringComparer.OrdinalIgnoreCase);
}
