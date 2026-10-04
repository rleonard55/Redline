using System.Text;

namespace Redline.Core.Diagnostics;

/// <summary>
/// Crash reports for unhandled exceptions: exception types, messages and stack traces plus version,
/// OS and uptime. No document text — Redline never puts user text into exception messages.
/// </summary>
public static class CrashReport
{
    /// <summary>Marker left beside a report from a terminating crash; the next start reports and removes it.</summary>
    public const string PendingMarker = "crash-pending";

    private const int MaxDepth = 8;

    public static string Format(Exception exception, string context, DateTimeOffset time, string version, TimeSpan uptime)
    {
        var sb = new StringBuilder();
        sb.Append("Redline crash report").AppendLine();
        sb.Append("Time:    ").Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).AppendLine();
        sb.Append("Context: ").Append(context).AppendLine();
        sb.Append("Version: ").Append(version).AppendLine();
        sb.Append("OS:      ").Append(Environment.OSVersion.VersionString).Append(Environment.Is64BitProcess ? " (x64 process)" : " (x86 process)").AppendLine();
        sb.Append("Runtime: ").Append(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription).AppendLine();
        sb.Append("Uptime:  ").Append(uptime.ToString(@"d\.hh\:mm\:ss")).AppendLine();
        sb.AppendLine();
        AppendException(sb, exception, 0);
        return sb.ToString();
    }

    /// <summary>Writes a report into <paramref name="directory"/>; with <paramref name="terminating"/>, also the pending marker.</summary>
    public static string Write(string directory, Exception exception, string context, bool terminating, string version, TimeSpan uptime)
    {
        var time = DateTimeOffset.Now;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"crash-{time:yyyyMMdd-HHmmss-fff}.txt");
        File.WriteAllText(path, Format(exception, context, time, version, uptime), Encoding.UTF8);
        if (terminating)
            File.WriteAllText(Path.Combine(directory, PendingMarker), Path.GetFileName(path), Encoding.UTF8);
        return path;
    }

    /// <summary>
    /// If the previous run ended in a crash, returns that report's path (or the folder) and clears the marker.
    /// </summary>
    public static string? TakePending(string directory)
    {
        var marker = Path.Combine(directory, PendingMarker);
        if (!File.Exists(marker)) return null;
        try
        {
            var name = File.ReadAllText(marker).Trim();
            File.Delete(marker);
            var report = Path.Combine(directory, Path.GetFileName(name));
            return name.Length > 0 && File.Exists(report) ? report : directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void AppendException(StringBuilder sb, Exception ex, int depth)
    {
        string indent = new(' ', depth * 2);
        sb.Append(indent).Append(ex.GetType().FullName).Append(" (HResult 0x").Append(ex.HResult.ToString("X8")).Append("): ")
          .Append(ex.Message).AppendLine();
        if (ex.StackTrace is { } stack)
        {
            foreach (var line in stack.Split('\n'))
                sb.Append(indent).Append("  ").Append(line.TrimEnd('\r').Trim()).AppendLine();
        }

        if (depth >= MaxDepth) return;
        IReadOnlyList<Exception> inner = ex is AggregateException agg ? agg.InnerExceptions : (ex.InnerException is { } i ? [i] : []);
        foreach (var child in inner)
        {
            sb.Append(indent).Append("--- inner ---").AppendLine();
            AppendException(sb, child, depth + 1);
        }
    }
}
