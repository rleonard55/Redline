using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Redline.Core.Diagnostics;
using Redline.Core.Interfaces;

namespace Redline.Windows.Automation;

/// <summary>
/// Detects text changes on one surface and reports fresh reads. Uses UIA TextChanged /
/// Value property events where the provider raises them, with polling as a safety net
/// (fast when no events have been seen, slow once events are known to work).
/// </summary>
/// <remarks>
/// UIA event callbacks arrive on the UIA client's callback thread. They only set a flag and
/// queue work — never make UIA calls or block there, because RemoveAutomationEventHandler
/// waits for in-flight callbacks and would deadlock against a blocked one.
/// </remarks>
public sealed class TextChangeWatcher : IDisposable
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SlowPoll = TimeSpan.FromSeconds(5);

    private readonly UiaDispatcher _uia;
    private readonly AutomationElement _element;
    private readonly ITextSurfaceAdapter _adapter;
    private readonly ILogger _logger;
    private readonly PerfCounters? _perf;
    private readonly Timer _pollTimer;

    private AutomationEventHandler? _textChangedHandler;
    private AutomationPropertyChangedEventHandler? _valueChangedHandler;

    private int _dirty;
    private int _reading;
    private volatile bool _stopped;
    private int _disposeCalled;
    private volatile bool _eventsSeen;
    private long _lastReadMs;

    public TextChangeWatcher(UiaDispatcher uia, AutomationElement element, ITextSurfaceAdapter adapter, ILogger logger, PerfCounters? perf = null)
    {
        _perf = perf;
        _uia = uia;
        _element = element;
        _adapter = adapter;
        _logger = logger;
        _pollTimer = new Timer(_ => OnPoll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised on a thread-pool thread with the full current text after each read.</summary>
    public event Action<string>? TextRead;

    /// <summary>Raised once if the element disappears or becomes unreadable.</summary>
    public event Action? SurfaceLost;

    public bool EventsSeen => _eventsSeen;

    /// <summary>Must be called on the UIA dispatcher thread.</summary>
    public void Start(bool supportsTextPattern, bool supportsValuePattern)
    {
        if (supportsTextPattern)
        {
            _textChangedHandler = (_, _) => OnEvent();
            System.Windows.Automation.Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, _element, TreeScope.Element, _textChangedHandler);
        }

        if (supportsValuePattern)
        {
            _valueChangedHandler = (_, _) => OnEvent();
            System.Windows.Automation.Automation.AddAutomationPropertyChangedEventHandler(_element, TreeScope.Element, _valueChangedHandler, ValuePattern.ValueProperty);
        }

        _pollTimer.Change(FastPoll, FastPoll);
        RequestRead(); // initial snapshot
    }

    private void OnEvent()
    {
        _eventsSeen = true;
        RequestRead();
    }

    private void OnPoll()
    {
        long sinceLastReadMs = Environment.TickCount64 - Interlocked.Read(ref _lastReadMs);
        if (_eventsSeen && sinceLastReadMs < SlowPoll.TotalMilliseconds)
            return;
        RequestRead();
    }

    /// <summary>Coalesces any number of requests into at most one in-flight read plus one follow-up.</summary>
    private void RequestRead()
    {
        if (_stopped) return;
        Interlocked.Exchange(ref _dirty, 1);
        if (Interlocked.CompareExchange(ref _reading, 1, 0) == 0)
            _ = ReadLoopAsync();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_stopped && Interlocked.Exchange(ref _dirty, 0) == 1)
            {
                var sw = Stopwatch.StartNew();
                var text = await _adapter.ReadTextAsync().ConfigureAwait(false);
                _perf?.Record("read", sw.Elapsed);
                Interlocked.Exchange(ref _lastReadMs, Environment.TickCount64);

                if (_stopped) return;
                if (text is null)
                {
                    _logger.LogInformation("Surface became unreadable: {Surface}", _adapter.Context);
                    _stopped = true; // stop reading; the owner disposes us
                    SurfaceLost?.Invoke();
                    return;
                }

                TextRead?.Invoke(text);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Read failed for {Surface}", _adapter.Context);
        }
        finally
        {
            Interlocked.Exchange(ref _reading, 0);
        }

        // A request that landed between the loop's last check and clearing _reading would be lost.
        if (!_stopped && Volatile.Read(ref _dirty) == 1)
            RequestRead();
    }

    /// <summary>
    /// Stops polling and unsubscribes on the UIA thread. Safe to call from any thread, including
    /// the UIA thread itself; does not block.
    /// </summary>
    public void Dispose()
    {
        _stopped = true;
        if (Interlocked.Exchange(ref _disposeCalled, 1) == 1)
            return;

        _pollTimer.Dispose();

        var text = _textChangedHandler;
        var value = _valueChangedHandler;
        _textChangedHandler = null;
        _valueChangedHandler = null;
        if (text is null && value is null)
            return;

        try
        {
            _ = _uia.InvokeAsync(() =>
            {
                try
                {
                    if (text is not null)
                        System.Windows.Automation.Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, _element, text);
                    if (value is not null)
                        System.Windows.Automation.Automation.RemoveAutomationPropertyChangedEventHandler(_element, value);
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or System.Runtime.InteropServices.COMException)
                {
                    // Element already gone; UIA drops its handlers with it.
                }
            });
        }
        catch (ObjectDisposedException)
        {
            // Shutting down: the UIA thread is gone and process exit releases the handlers.
        }
    }
}
