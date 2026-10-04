using System.Text;

namespace Redline.Core.Diagnostics;

/// <summary>
/// The log folder: daily files (redline-yyyyMMdd.log, rolling to -1, -2… when a file reaches
/// <see cref="MaxFileBytes"/>) plus crash reports, pruned to <see cref="MaxAge"/> and
/// <see cref="MaxTotalBytes"/>. Callers must never pass user text.
/// </summary>
/// <remarks>Not thread-safe; the owner serializes calls.</remarks>
public sealed class LogFiles
{
    public const string LogPattern = "redline-*.log";
    public const string CrashPattern = "crash-*.txt";

    private readonly Func<DateTime> _now;
    private string? _currentPath;
    private DateTime _currentDay;
    private long _currentBytes;

    public LogFiles(string directory, long maxFileBytes = 10 * 1024 * 1024, long maxTotalBytes = 50 * 1024 * 1024,
        TimeSpan? maxAge = null, Func<DateTime>? now = null)
    {
        Directory = directory;
        MaxFileBytes = maxFileBytes;
        MaxTotalBytes = maxTotalBytes;
        MaxAge = maxAge ?? TimeSpan.FromDays(7);
        _now = now ?? (() => DateTime.Now);
        System.IO.Directory.CreateDirectory(directory);
    }

    public string Directory { get; }
    public long MaxFileBytes { get; }
    public long MaxTotalBytes { get; }
    public TimeSpan MaxAge { get; }

    /// <summary>The file the next <see cref="Append"/> wrote to, if any.</summary>
    public string? CurrentPath => _currentPath;

    /// <summary>Appends <paramref name="text"/> to today's file, rolling over (and pruning) when needed.</summary>
    public void Append(string text)
    {
        var now = _now();
        long bytes = Encoding.UTF8.GetByteCount(text);

        if (_currentPath is null || now.Date != _currentDay || _currentBytes + bytes > MaxFileBytes)
        {
            _currentDay = now.Date;
            (_currentPath, _currentBytes) = NextFile(now.Date, bytes);
            Prune();
        }

        File.AppendAllText(_currentPath, text, Encoding.UTF8);
        _currentBytes += bytes;
    }

    /// <summary>
    /// Deletes files older than <see cref="MaxAge"/>, then the oldest ones until the folder fits in
    /// <see cref="MaxTotalBytes"/>. The current log file is never deleted. Returns how many were removed.
    /// </summary>
    public int Prune()
    {
        var cutoff = _now() - MaxAge;
        var current = _currentPath is null ? null : new FileInfo(_currentPath);
        var files = System.IO.Directory.EnumerateFiles(Directory, LogPattern)
            .Concat(System.IO.Directory.EnumerateFiles(Directory, CrashPattern))
            .Select(p => new FileInfo(p))
            .Where(f => !string.Equals(f.FullName, current?.FullName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.LastWriteTime)
            .ToList();

        long total = files.Sum(f => f.Length) + (current is { Exists: true } ? current.Length : 0);
        int removed = 0;
        foreach (var f in files)
        {
            if (f.LastWriteTime >= cutoff && total <= MaxTotalBytes) break;
            try
            {
                long length = f.Length;
                f.Delete();
                total -= length;
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or locked down; try again next time.
            }
        }
        return removed;
    }

    /// <summary>First file for <paramref name="day"/> that has room for <paramref name="bytes"/> more.</summary>
    private (string Path, long Bytes) NextFile(DateTime day, long bytes)
    {
        for (int i = 0; ; i++)
        {
            var path = Path.Combine(Directory, i == 0 ? $"redline-{day:yyyyMMdd}.log" : $"redline-{day:yyyyMMdd}-{i}.log");
            long length = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (length == 0 || length + bytes <= MaxFileBytes)
                return (path, length);
        }
    }
}
