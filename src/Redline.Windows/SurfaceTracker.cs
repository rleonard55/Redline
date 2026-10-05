using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using Redline.Core.Diagnostics;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Pipeline;
using Redline.Windows.Automation;

namespace Redline.Windows;

public sealed record SurfaceChangedEventArgs(TextSurfaceContext? Surface, TextSurfaceCapabilities? Capabilities, string Reason);

/// <summary>A focused field Redline declined to read. Identity only (no text, no title).</summary>
public sealed record SurfaceBlockedEventArgs(string ProcessName, string ControlType, string ClassName, string FrameworkId, string Reason, bool Sensitive);

public sealed record SnapshotChangedEventArgs(TextSurfaceContext Surface, TextSnapshot Snapshot, TextChange? Change);

/// <summary>
/// Follows keyboard focus across applications and keeps exactly one surface attached:
/// focus/foreground event → security filter → adapter selection → change watcher → snapshots.
/// </summary>
/// <remarks>
/// All attach/detach state is touched only on the UIA dispatcher thread, so it needs no locks.
/// Focus events are coalesced by generation number: when focus moves quickly, only the latest
/// element is evaluated. Snapshot events are raised on thread-pool threads.
/// </remarks>
public sealed class SurfaceTracker : IDisposable
{
    private readonly UiaDispatcher _uia;
    private readonly FocusMonitor _focus;
    private readonly ForegroundWindowMonitor _foreground;
    private readonly AdapterSelector _selector;
    private readonly SecurityFilter _security;
    private readonly DocumentState _document;
    private readonly ILogger<SurfaceTracker> _logger;
    private readonly PerfCounters? _perf;

    private long _focusGeneration;
    private volatile bool _paused;

    // UIA thread only.
    private volatile ITextSurfaceAdapter? _adapter;
    private TextChangeWatcher? _watcher;
    private string? _lastDetachReason;
    private long _deferredDetach; // id of the pending deferred detach; 0 = none (UIA thread only)
    private long _detachSequence;

    /// <summary>
    /// How long focus may sit on a non-text element in the same app before we let go of the surface.
    /// Web apps and IDEs bounce focus through their page/container for a moment when re-activated.
    /// </summary>
    private static readonly TimeSpan DetachGrace = TimeSpan.FromMilliseconds(300);

    public SurfaceTracker(
        UiaDispatcher uia,
        FocusMonitor focus,
        ForegroundWindowMonitor foreground,
        AdapterSelector selector,
        SecurityFilter security,
        DocumentState document,
        ILogger<SurfaceTracker> logger,
        PerfCounters? perf = null)
    {
        _perf = perf;
        _uia = uia;
        _focus = focus;
        _foreground = foreground;
        _selector = selector;
        _security = security;
        _document = document;
        _logger = logger;
    }

    public event EventHandler<SurfaceChangedEventArgs>? SurfaceChanged;
    public event EventHandler<SnapshotChangedEventArgs>? SnapshotChanged;

    /// <summary>Raised on the UIA thread each time a focused field is blocked (for the compatibility record).</summary>
    public event EventHandler<SurfaceBlockedEventArgs>? SurfaceBlocked;

    public TextSurfaceContext? CurrentSurface => _adapter?.Context;

    /// <summary>The attached surface's adapter, or null. Safe from any thread; may be detached at any moment.</summary>
    public ITextSurfaceAdapter? CurrentAdapter => _adapter;

    public bool IsPaused => _paused;

    public async Task StartAsync()
    {
        _focus.FocusChanged += element => Schedule("focus event", () => element);
        _foreground.ForegroundChanged += hwnd => _ = VerifyForegroundFocusAsync(hwnd);

        await _focus.StartAsync().ConfigureAwait(false);
        _foreground.Start();
        Schedule("startup", () => AutomationElement.FocusedElement);
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
        {
            Interlocked.Increment(ref _focusGeneration); // drop any queued evaluations
            _ = _uia.InvokeAsync(() => Detach("Paused"));
        }
        else
        {
            Schedule("resume", () => AutomationElement.FocusedElement);
        }
    }

    /// <summary>
    /// Drops the current surface and evaluates the focused element again, e.g. after the exclusion list
    /// changed (an attached surface otherwise isn't re-checked while it keeps focus).
    /// </summary>
    public void Reevaluate()
    {
        if (_paused) return;
        _ = _uia.InvokeAsync(() => Detach("Settings changed"));
        Schedule("settings", () => AutomationElement.FocusedElement);
    }

    /// <summary>Queues evaluation of the element produced by <paramref name="getElement"/> (run on the UIA thread).</summary>
    private void Schedule(string source, Func<AutomationElement?> getElement)
    {
        if (_paused) return;
        long generation = Interlocked.Increment(ref _focusGeneration);
        try
        {
            _ = _uia.InvokeAsync(() =>
            {
                if (generation != Interlocked.Read(ref _focusGeneration) || _paused)
                    return; // superseded by a newer focus change

                try
                {
                    var element = getElement();
                    if (element is not null)
                        Evaluate(element, source);
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or System.Runtime.InteropServices.COMException)
                {
                    // Focus moved to something that vanished; the next event will correct us.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed evaluating focused element");
                }
            });
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Fallback for apps that activate without raising a UIA focus event. UIA focus events stay
    /// authoritative: this never supersedes them, it only re-checks global focus once it has had
    /// time to settle and belongs to the window that came to the foreground. Querying immediately
    /// would see the previous app's element, look like "same surface", and silently miss the switch.
    /// </summary>
    private async Task VerifyForegroundFocusAsync(IntPtr hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out uint foregroundPid);

        foreach (var delay in ForegroundSettleDelays)
        {
            await Task.Delay(delay).ConfigureAwait(false);
            if (_paused || GetForegroundWindow() != hwnd)
                return; // paused, or another switch happened and has its own check

            bool settled;
            try
            {
                settled = await _uia.InvokeAsync(() =>
                {
                    // A focus event already attached a surface in this app — nothing to fall back for,
                    // and re-evaluating could swap to a different element for the same editor.
                    if (_adapter is not null && _adapter.Context.ProcessId == foregroundPid)
                        return true;

                    var element = AutomationElement.FocusedElement;
                    if (element is null || element.Current.ProcessId != foregroundPid)
                        return false; // global focus hasn't reached the new window yet

                    Evaluate(element, "foreground fallback");
                    return true;
                }).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return; // shutting down
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or System.Runtime.InteropServices.COMException)
            {
                settled = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed verifying focus after foreground change");
                return;
            }

            if (settled) return;
        }
    }

    private static readonly TimeSpan[] ForegroundSettleDelays =
        [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)];

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>UIA thread only.</summary>
    private void Evaluate(AutomationElement element, string source)
    {
        var info = ElementInfo.Capture(element);
        _logger.LogDebug("Evaluating ({Source}): {Process} {ControlType}/{ClassName} id={SurfaceId}",
            source, info.ProcessName, info.ControlType, info.ClassName, info.SurfaceId);

        // Chromium (Teams, ChatGPT, Electron apps) sends a stray focus event for a hidden,
        // non-focusable helper Edit ~200-500 ms after the real one, while keyboard focus stays in
        // the editor. Something that can't take keyboard focus isn't where the user is typing,
        // so trust UIA's global focus instead.
        if (source == "focus event" && !info.IsKeyboardFocusable)
        {
            var actual = AutomationElement.FocusedElement;
            if (actual is not null)
            {
                var actualInfo = ElementInfo.Capture(actual);
                if (actualInfo.SurfaceId != info.SurfaceId)
                {
                    _logger.LogDebug("Ignoring focus event for non-focusable {ControlType}; global focus is {ActualType}/{ActualClass}",
                        info.ControlType, actualInfo.ControlType, actualInfo.ClassName);
                    element = actual;
                    info = actualInfo;
                }
            }
        }

        // Clicking Redline's own diagnostics window shouldn't drop the surface being inspected.
        if (info.ProcessId == Environment.ProcessId)
            return;

        if (_adapter is not null && _adapter.Context.SurfaceId == info.SurfaceId)
        {
            _deferredDetach = 0; // focus came back before the grace period ran out
            return;
        }

        var decision = _security.Evaluate(info);
        if (!decision.Allowed)
        {
            var reason = $"Blocked: {decision.Reason} ({info.ProcessName} {info.ControlType}/{info.ClassName})";
            SurfaceBlocked?.Invoke(this, new SurfaceBlockedEventArgs(info.ProcessName, info.ControlType, info.ClassName, info.FrameworkId, decision.Reason, decision.Sensitive));
            if (decision.Sensitive) Detach(reason);
            else DetachSoon(info, reason);
            return;
        }

        var factory = _selector.Select(info);
        if (factory is null)
        {
            // Control type/class/process carry no user text and are what's needed to diagnose a miss.
            DetachSoon(info, $"No text surface focused ({info.ProcessName} {info.ControlType}/{info.ClassName})");
            return;
        }

        Attach(element, info, factory);
    }

    /// <summary>UIA thread only.</summary>
    private void Attach(AutomationElement element, ElementInfo info, ITextSurfaceAdapterFactory factory)
    {
        _deferredDetach = 0;
        DetachCore();

        var adapter = factory.Create(_uia, element, info);
        var watcher = new TextChangeWatcher(_uia, element, adapter, _logger, _perf);
        _adapter = adapter;
        _watcher = watcher;
        _lastDetachReason = null;

        var surface = adapter.Context;
        _document.Reset(surface.SurfaceId);

        watcher.TextRead += text =>
        {
            var update = _document.Update(surface.SurfaceId, text);
            if (update is { } u)
                SnapshotChanged?.Invoke(this, new SnapshotChangedEventArgs(surface, u.Snapshot, u.Change));
        };
        watcher.SurfaceLost += () =>
        {
            try
            {
                _ = _uia.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_watcher, watcher))
                        Detach("Surface no longer readable");
                });
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            Schedule("surface lost", () => AutomationElement.FocusedElement);
        };

        _logger.LogInformation("Attached {Surface} (patterns: {Patterns})", surface, string.Join(",", adapter.Capabilities.SupportedPatterns));
        SurfaceChanged?.Invoke(this, new SurfaceChangedEventArgs(surface, adapter.Capabilities, "Focused"));

        try
        {
            watcher.Start(info.SupportsTextPattern, info.SupportsValuePattern);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or System.Runtime.InteropServices.COMException)
        {
            Detach("Surface vanished while attaching");
        }
    }

    /// <summary>
    /// UIA thread only. Detaches after <see cref="DetachGrace"/> if focus moved to a non-text element of
    /// the same app; immediately otherwise. Never used for sensitive blocks.
    /// </summary>
    private void DetachSoon(ElementInfo focused, string reason)
    {
        if (_adapter is null || focused.ProcessId != _adapter.Context.ProcessId)
        {
            Detach(reason);
            return;
        }

        long id = ++_detachSequence;
        _deferredDetach = id;
        _logger.LogDebug("Deferring detach {Grace} ms: {Reason}", DetachGrace.TotalMilliseconds, reason);
        _ = Task.Delay(DetachGrace).ContinueWith(_ =>
        {
            try
            {
                _uia.InvokeAsync(() =>
                {
                    if (_deferredDetach == id)
                        Detach(reason);
                });
            }
            catch (ObjectDisposedException)
            {
                // shutting down
            }
        }, TaskScheduler.Default);
    }

    /// <summary>UIA thread only.</summary>
    private void Detach(string reason)
    {
        _deferredDetach = 0;
        bool hadSurface = _adapter is not null;
        DetachCore();
        _document.Reset(null);

        // Only notify on transitions, not on every focus hop between non-text controls.
        if (hadSurface || reason != _lastDetachReason)
        {
            _lastDetachReason = reason;
            _logger.LogInformation("Detached: {Reason}", reason);
            SurfaceChanged?.Invoke(this, new SurfaceChangedEventArgs(null, null, reason));
        }
    }

    private void DetachCore()
    {
        _watcher?.Dispose();
        _adapter?.Dispose();
        _watcher = null;
        _adapter = null;
    }

    public void Dispose()
    {
        _paused = true;
        _foreground.Dispose();
        _focus.Dispose();
        try
        {
            _uia.InvokeAsync(DetachCore).Wait(1000);
        }
        catch
        {
            // Dispatcher already gone.
        }
    }
}
