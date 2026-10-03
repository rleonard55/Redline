using System.IO;
using Microsoft.Extensions.Logging;

namespace Redline.App.Logging;

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss.fff} {Level,-11} {Category}: {Message}";
}

/// <summary>
/// In-memory ring buffer for the diagnostics window plus a daily file under
/// %LOCALAPPDATA%\Redline\logs. Callers must never log user text (see Cross-Cutting: Logging).
/// </summary>
public sealed class DiagnosticsLog : ILoggerProvider
{
    private const int Capacity = 500;
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new();
    private readonly string? _logDirectory;

    public DiagnosticsLog(string? logDirectory)
    {
        _logDirectory = logDirectory;
        if (logDirectory is not null)
            Directory.CreateDirectory(logDirectory);
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Redline", "logs");

    public event Action<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    public void Write(LogLevel level, string category, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, category, message);
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity) _entries.Dequeue();

            if (_logDirectory is not null)
            {
                try
                {
                    File.AppendAllText(Path.Combine(_logDirectory, $"redline-{entry.Time:yyyyMMdd}.log"), entry + Environment.NewLine);
                }
                catch (IOException)
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
            // Type + message keeps lines readable; no stack traces until Phase 5's structured crash logging.
            if (exception is not null)
                message += $" [{exception.GetType().Name}: {exception.Message}]";
            log.Write(logLevel, category, message);
        }
    }
}
