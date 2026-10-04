using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Redline.Core.Geometry;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Pipeline;
using Redline.Windows;
using Redline.Windows.Automation;

namespace Redline.Annotations;

/// <summary>
/// Keeps squiggles under the current surface's issues. Shows them only while the target window is
/// in front; hides them whenever their position is uncertain (window being dragged, text edited,
/// geometry unverifiable) and redraws once things settle (risk R3).
/// </summary>
/// <remarks>
/// Triggers: new issues, text edits (existing squiggles shift immediately; the edited word's is
/// dropped until re-analysis), window move/resize/minimize events, and a periodic refresh that
/// catches scrolling, which raises no reliable window event. All state lives on the UI thread;
/// geometry queries run on the UIA thread and are re-validated when they return.
/// </remarks>
public sealed class OverlayManager : IDisposable
{
    private const int MaxIssuesDrawn = 150;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);

    private readonly Dispatcher _ui;
    private readonly SurfaceTracker _tracker;
    private readonly DocumentState _document;
    private readonly IssueCacheManager _cache;
    private readonly WindowEventMonitor _windowEvents;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _refresh;
    private readonly DispatcherTimer _settle;
    private readonly int _ownProcessId = Environment.ProcessId;

    // UI thread only.
    private OverlayWindow? _window;
    private ITextSurfaceAdapter? _adapter;
    private IntPtr _root;
    private IssueSet? _issues;
    private bool _moving;
    private bool _layoutRunning;
    private bool _layoutAgain;
    private int _lastSpanCount = -1;

    public OverlayManager(Dispatcher ui, SurfaceTracker tracker, DocumentState document, IssueCacheManager cache,
        WindowEventMonitor windowEvents, ILogger<OverlayManager>? logger = null)
    {
        _ui = ui;
        _tracker = tracker;
        _document = document;
        _cache = cache;
        _windowEvents = windowEvents;
        _logger = logger ?? NullLogger<OverlayManager>.Instance;

        _refresh = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => RequestLayout(), _ui);
        _settle = new DispatcherTimer(SettleDelay, DispatcherPriority.Normal, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            _moving = false;
            RequestLayout();
        }, _ui);
    }

    /// <summary>Call on the UI thread.</summary>
    public void Start()
    {
        _tracker.SurfaceChanged += (_, e) => _ui.BeginInvoke(() => OnSurfaceChanged(e));
        _tracker.SnapshotChanged += (_, e) => _ui.BeginInvoke(() => OnSnapshotChanged(e));
        _cache.IssuesChanged += (_, e) => _ui.BeginInvoke(() => OnIssuesChanged(e));
        _windowEvents.Changed += (hwnd, change) => _ui.BeginInvoke(() => OnWindowChanged(hwnd, change));
        _refresh.Start();
    }

    private async void OnSurfaceChanged(SurfaceChangedEventArgs e)
    {
        Hide();
        _issues = null;
        _adapter = e.Surface is null ? null : _tracker.CurrentAdapter;
        _root = IntPtr.Zero;
        _windowEvents.Track(IntPtr.Zero);
        if (_adapter is null) return;

        var adapter = _adapter;
        var root = new IntPtr(await adapter.GetTopLevelWindowAsync());
        if (!ReferenceEquals(adapter, _adapter)) return; // focus moved on meanwhile
        _root = root;
        _windowEvents.Track(root);

        // Issues for this surface may already be cached from earlier in the session.
        if (_cache.Get(adapter.Context.SurfaceId, _document.Current?.Version) is { } cached)
            _issues = cached;
        RequestLayout();
    }

    private void OnIssuesChanged(IssuesChangedEventArgs e)
    {
        if (_adapter?.Context.SurfaceId != e.SurfaceId) return;
        _issues = e.Issues;
        RequestLayout();
    }

    private void OnSnapshotChanged(SnapshotChangedEventArgs e)
    {
        if (_adapter?.Context.SurfaceId != e.Surface.SurfaceId || _issues is null) return;

        // Shift squiggles to follow the edit right away instead of blanking them all until the
        // re-analysis lands. Anything touching the edit is dropped: that word is being changed.
        if (e.Change is not { } c)
        {
            _issues = null;
        }
        else
        {
            int oldEnd = c.Start + c.OldLength;
            var kept = _issues.Issues
                .Where(i => i.StartOffset + i.Length < c.Start || i.StartOffset > oldEnd)
                .Select(i => i.StartOffset > oldEnd ? i with { StartOffset = i.StartOffset + c.Delta } : i)
                .Select(i => i with { SnapshotVersion = e.Snapshot.Version })
                .ToList();
            _issues = new IssueSet(kept, e.Snapshot.Version);
        }
        RequestLayout();
    }

    private void OnWindowChanged(IntPtr hwnd, WindowChange change)
    {
        if (hwnd != _root) return;
        switch (change)
        {
            case WindowChange.MoveSizeStart:
            case WindowChange.Minimized:
                _moving = true;
                _settle.Stop();
                Hide();
                break;

            case WindowChange.Moved:
                // Fires continuously while dragging, and on maximize/snap without MoveSizeStart.
                _moving = true;
                Hide();
                _settle.Stop();
                _settle.Start();
                break;

            case WindowChange.MoveSizeEnd:
            case WindowChange.Restored:
                _settle.Stop();
                _settle.Start();
                break;
        }
    }

    /// <summary>Coalesces requests: at most one layout in flight, plus one follow-up.</summary>
    private async void RequestLayout()
    {
        if (_layoutRunning)
        {
            _layoutAgain = true;
            return;
        }

        _layoutRunning = true;
        try
        {
            do
            {
                _layoutAgain = false;
                await LayoutAsync();
            } while (_layoutAgain);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Overlay layout failed");
            Hide();
        }
        finally
        {
            _layoutRunning = false;
        }
    }

    private async Task LayoutAsync()
    {
        var adapter = _adapter;
        var issues = _issues;
        var snapshot = _document.Current;

        if (adapter is null || issues is null || snapshot is null || _moving || _root == IntPtr.Zero ||
            issues.SnapshotVersion != snapshot.Version || issues.Issues.Count == 0)
        {
            Hide();
            return;
        }

        // Only while the target is in front. Redline's own popup/diagnostics being in front leaves
        // the squiggles as they are (they're what the user is acting on).
        var foreground = GetForegroundWindow();
        if (foreground != _root)
        {
            GetWindowThreadProcessId(foreground, out uint pid);
            if (pid != _ownProcessId) Hide();
            return;
        }

        var sw = Stopwatch.StartNew();
        var surface = await adapter.GetSurfaceBoundsAsync();
        var drawn = issues.Issues.Take(MaxIssuesDrawn).ToList();
        var bounds = await adapter.GetBoundsAsync(drawn.Select(i => i.Range).ToList(), snapshot.Text);

        // Anything may have changed while we were away on the UIA thread.
        if (!ReferenceEquals(adapter, _adapter) || !ReferenceEquals(issues, _issues) ||
            _document.Current?.Version != snapshot.Version || _moving)
        {
            _layoutAgain = true;
            return;
        }

        if (surface is null)
        {
            Hide();
            return;
        }

        var spans = OverlayLayout.Layout(surface.Value, drawn.Select((issue, i) => (issue.Category, bounds[i])));
        if (spans.Count == 0)
        {
            Hide();
            return;
        }

        _window ??= new OverlayWindow();
        _window.ShowAt(surface.Value, spans, _root);

        if (spans.Count != _lastSpanCount)
        {
            _lastSpanCount = spans.Count;
            int unplaced = bounds.Count(b => b.Count == 0);
            _logger.LogDebug("Overlay: {Spans} squiggle(s) for {Issues} issue(s) ({Unplaced} off-screen or unverified) in {Ms:F0} ms",
                spans.Count, drawn.Count, unplaced, sw.Elapsed.TotalMilliseconds);
        }
    }

    private void Hide()
    {
        _window?.HideOverlay();
        _lastSpanCount = -1;
    }

    public void Dispose()
    {
        _refresh.Stop();
        _settle.Stop();
        _window?.Close();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
