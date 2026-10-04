using System.IO;
using Microsoft.Extensions.Logging;
using Redline.Core.Diagnostics;

namespace Redline.App.Logging;

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss.fff} {Level,-11} {Category}: {Message}";
}

/// <summary>
/// In-memory ring buffer for the diagnostics window (every level) plus daily files under
/// %LOCALAPPDATA%\Redline\logs (<see cref="FileLevel"/> and up; rotation and retention in
/// <see cref="LogFiles"/>). Callers must never log user text (see Cross-Cutting: Logging).
/// </summary>
public sealed class DiagnosticsLog : ILoggerProvider
{
    private const int Capacity = 500;
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new();
    private readonly LogFiles? _files;

    public DiagnosticsLog(string? logDirectory)
    {
        LogDirectory = logDirectory;
        if (logDirectory is null) return;
        try
        {
            _files = new LogFiles(logDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No log files then; the diagnostics window still works.
        }
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Redline", "logs");

    public string? LogDirectory { get; }

    /// <summary>Lowest level written to the log file: Information normally, Debug in diagnostics mode.</summary>
    public LogLevel FileLevel { get; set; } = LogLevel.Information;

    public event Action<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    public void Write(LogLevel level, string category, string message, string? fileDetail = null)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, category, message);
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity) _entries.Dequeue();

            if (_files is not null && level >= FileLevel)
            {
                try
                {
                    _files.Append(entry + Environment.NewLine + (fileDetail ?? string.Empty));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Logging must never take the app down.
                }
            }
        }
        EntryAdded?.Invoke(entry);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, ShortName(categoryName));

    public void Dispose() { }

    private static string ShortName(string category)
    {
        int dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private sealed class Logger(DiagnosticsLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            string? detail = null;
            // Type + message keeps lines readable; errors also get the stack trace, in the file only.
            if (exception is not null)
            {
                var exMsg = exception.InnerException is not null
                    ? $"{exception.GetType().Name}: {exception.Message} -> {exception.InnerException.GetType().Name}: {exception.InnerException.Message}"
                    : $"{exception.GetType().Name}: {exception.Message}";
                message += $" [{exMsg}]";
                if (logLevel >= LogLevel.Error)
                    detail = Indent(exception.ToString());
            }
            log.Write(logLevel, category, message, detail);
        }

        private static string Indent(string text) =>
            string.Concat(text.Split('\n').Select(l => "    " + l.TrimEnd('\r') + Environment.NewLine));
    }
}
