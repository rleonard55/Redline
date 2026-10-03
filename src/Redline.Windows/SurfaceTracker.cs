using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Pipeline;
using Redline.Windows.Automation;

namespace Redline.Windows;

public sealed record SurfaceChangedEventArgs(TextSurfaceContext? Surface, TextSurfaceCapabilities? Capabilities, string Reason);

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

    private long _focusGeneration;
    private volatile bool _paused;

    // UIA thread only.
    private ITextSurfaceAdapter? _adapter;
    private TextChangeWatcher? _watcher;
    private string? _lastDetachReason;

    public SurfaceTracker(
        UiaDispatcher uia,
        FocusMonitor focus,
        ForegroundWindowMonitor foreground,
        AdapterSelector selector,
        SecurityFilter security,
        DocumentState document,
        ILogger<SurfaceTracker> logger)
    {
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

    public TextSurfaceContext? CurrentSurface => _adapter?.Context;

    public bool IsPaused => _paused;

    public async Task StartAsync()
    {
        _focus.FocusChanged += element => Schedule(() => element);
        _foreground.ForegroundChanged += _ => Schedule(() => AutomationElement.FocusedElement);

        await _focus.StartAsync().ConfigureAwait(false);
        _foreground.Start();
        Schedule(() => AutomationElement.FocusedElement);
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
            Schedule(() => AutomationElement.FocusedElement);
        }
    }

    /// <summary>Queues evaluation of the element produced by <paramref name="getElement"/> (run on the UIA thread).</summary>
    private void Schedule(Func<AutomationElement?> getElement)
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
                        Evaluate(element);
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

    /// <summary>UIA thread only.</summary>
    private void Evaluate(AutomationElement element)
    {
        var info = ElementInfo.Capture(element);

        // Clicking Redline's own diagnostics window shouldn't drop the surface being inspected.
        if (info.ProcessId == Environment.ProcessId)
            return;

        if (_adapter is not null && _adapter.Context.SurfaceId == info.SurfaceId)
            return;

        var decision = _security.Evaluate(info);
        if (!decision.Allowed)
        {
            Detach($"Blocked: {decision.Reason}");
            return;
        }

        var factory = _selector.Select(info);
        if (factory is null)
        {
            Detach("No text surface focused");
            return;
        }

        Attach(element, info, factory);
    }

    /// <summary>UIA thread only.</summary>
    private void Attach(AutomationElement element, ElementInfo info, ITextSurfaceAdapterFactory factory)
    {
        DetachCore();

        var adapter = factory.Create(_uia, element, info);
        var watcher = new TextChangeWatcher(_uia, element, adapter, _logger);
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
            Schedule(() => AutomationElement.FocusedElement);
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

    /// <summary>UIA thread only.</summary>
    private void Detach(string reason)
    {
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
