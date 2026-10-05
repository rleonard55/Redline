using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Redline.Core.Corrections;
using Redline.Core.Diagnostics;
using Redline.Core.Geometry;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Pipeline;
using Redline.Windows;
using Redline.Windows.Automation;

namespace Redline.Annotations;

/// <summary>A gutter pill was clicked: fix <paramref name="Paragraph"/> of snapshot <paramref name="SnapshotVersion"/>.</summary>
/// <param name="Anchor">The paragraph's visible extent (physical pixels), to place the fix popup beside.</param>
public sealed record ParagraphFixRequest(TextRange Paragraph, long SnapshotVersion, TextBounds Anchor);

/// <summary>
/// Keeps squiggles under the current surface's issues. Shows them only while the target window is
/// in front; hides them whenever their position is uncertain (window being dragged, text edited,
/// geometry unverifiable) and redraws once things settle (risk R3).
/// </summary>
/// <remarks>
/// Triggers: new issues, text edits (existing squiggles shift immediately; the edited word's is
/// dropped until re-analysis), window move/resize/minimize events, and a periodic refresh.
/// Scrolling raises no reliable event, so a cheap 100 ms probe re-measures one squiggled word (the
/// anchor): if it moved, squiggles hide at once and come back when it holds still. All state lives
/// on the UI thread; geometry queries run on the UIA thread and are re-validated when they return.
/// </remarks>
public sealed class OverlayManager : IDisposable
{
    private const int MaxIssuesDrawn = 150;
    private const int MaxGutterPills = 30;

    /// <summary>A paragraph gets a gutter pill when it has at least this many fixes (one fix is the hover pill's job).</summary>
    public const int GutterMinChanges = 2;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan AnchorInterval = TimeSpan.FromMilliseconds(100);

    private readonly Dispatcher _ui;
    private readonly SurfaceTracker _tracker;
    private readonly DocumentState _document;
    private readonly IssueCacheManager _cache;
    private readonly WindowEventMonitor _windowEvents;
    private readonly ILogger _logger;
    private readonly PerfCounters? _perf;
    private readonly CompatibilityLog? _compatibility;
    private readonly DispatcherTimer _refresh;
    private readonly DispatcherTimer _settle;
    private readonly DispatcherTimer _anchorTimer;
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly HoverController _hover;

    // UI thread only.
    private OverlayWindow? _window;
    private ITextSurfaceAdapter? _adapter;
    private IntPtr _root;
    private IssueSet? _issues;
    private bool _moving;
    private bool _layoutRunning;
    private bool _layoutAgain;
    private int _lastSpanCount = -1;
    private readonly List<GutterPill> _gutterPills = new();
    private bool _gutterEnabled = true;

    // Scroll detection: one drawn word's range and where it was when last drawn.
    private (TextRange Range, TextBounds Rect)? _anchor;
    private bool _scrolling;
    private bool _anchorCheckRunning;

    public OverlayManager(Dispatcher ui, SurfaceTracker tracker, DocumentState document, IssueCacheManager cache,
        WindowEventMonitor windowEvents, ILogger<OverlayManager>? logger = null, PerfCounters? perf = null,
        CompatibilityLog? compatibility = null)
    {
        _perf = perf;
        _compatibility = compatibility;
        _ui = ui;
        _tracker = tracker;
        _document = document;
        _cache = cache;
        _windowEvents = windowEvents;
        _logger = logger ?? NullLogger<OverlayManager>.Instance;
        _hover = new HoverController(ui);
        _hover.ApplyRequested += issue => ApplyRequested?.Invoke(issue);
        _hover.MoreRequested += issue => MoreRequested?.Invoke(issue);

        // Note: the DispatcherTimer constructor overload that takes a callback also *starts* the timer,
        // so these use the (priority, dispatcher) overload and are started explicitly in Start().
        _refresh = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = RefreshInterval };
        _refresh.Tick += (_, _) => RequestLayout();
        _anchorTimer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = AnchorInterval };
        _anchorTimer.Tick += (_, _) => CheckAnchor();
        _settle = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = SettleDelay };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _moving = false;
            RequestLayout();
        };
    }

    /// <summary>The hover pill's suggestion was clicked: apply the issue's first suggestion. Raised on the UI thread.</summary>
    public event Action<TextIssue>? ApplyRequested;

    /// <summary>The hover pill's "⋯" was clicked: show the full suggestion popup. Raised on the UI thread.</summary>
    public event Action<TextIssue>? MoreRequested;

    /// <summary>A paragraph's gutter pill was clicked. Raised on the UI thread.</summary>
    public event Action<ParagraphFixRequest>? ParagraphFixRequested;

    /// <summary>Show a bar in the left gutter of paragraphs with several fixes. UI thread.</summary>
    public bool GutterEnabled
    {
        get => _gutterEnabled;
        set
        {
            if (_gutterEnabled == value) return;
            _gutterEnabled = value;
            if (!value) HideGutter();
            RequestLayout();
        }
    }

    /// <summary>Show the quick-fix pill when the pointer rests on a squiggle. UI thread.</summary>
    public bool HoverEnabled
    {
        get => _hover.Enabled;
        set => _hover.Enabled = value;
    }

    /// <summary>Call on the UI thread.</summary>
    public void Start()
    {
        _tracker.SurfaceChanged += (_, e) => _ui.BeginInvoke(() => OnSurfaceChanged(e));
        _tracker.SnapshotChanged += (_, e) => _ui.BeginInvoke(() => OnSnapshotChanged(e));
        _cache.IssuesChanged += (_, e) => _ui.BeginInvoke(() => OnIssuesChanged(e));
        _windowEvents.Changed += (hwnd, change) => _ui.BeginInvoke(() => OnWindowChanged(hwnd, change));
        _refresh.Start();
        _anchorTimer.Start();
    }

    private async void OnSurfaceChanged(SurfaceChangedEventArgs e)
    {
        Hide();
        _hover.Clear(disarm: true);
        _scrolling = false;
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
        _anchor = null; // offsets changed; the next layout picks a new anchor
        RequestLayout();
    }

    private void OnSnapshotChanged(SnapshotChangedEventArgs e)
    {
        if (_adapter?.Context.SurfaceId != e.Surface.SurfaceId || _issues is null) return;
        _anchor = null; // its offsets refer to the old text
        _hover.Clear(disarm: true); // typing: the pill waits for the pointer to move again

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

        if (_scrolling)
            return; // CheckAnchor re-requests layout once the text holds still

        if (adapter is null || issues is null || snapshot is null || _moving || _root == IntPtr.Zero ||
            issues.SnapshotVersion != snapshot.Version || issues.Issues.Count == 0)
        {
            Hide();
            return;
        }

        // Only while the target is in front. Redline's own popup/diagnostics being in front leaves
        // the squiggles as they are (they're what the user is acting on).
        // Root of the foreground window: Chromium can report its page child window as foreground.
        // That's still the target for display purposes (typing uses a stricter check).
        var foreground = GetAncestor(GetForegroundWindow(), GA_ROOT);
        if (foreground != _root)
        {
            GetWindowThreadProcessId(foreground, out uint pid);
            if (pid != _ownProcessId) Hide();
            return;
        }

        var sw = Stopwatch.StartNew();
        var surface = await adapter.GetSurfaceBoundsAsync();
        // Spelling and grammar sometimes flag the same word; draw one squiggle per range, not two
        // overlapping ones (spelling wins, then grammar, punctuation, style).
        var drawn = issues.Issues
            .GroupBy(i => i.Range)
            .Select(g => g.OrderBy(i => CategoryPriority(i.Category)).First())
            .Take(MaxIssuesDrawn)
            .ToList();
        // Paragraphs worth a gutter pill are measured in the same round trip as the squiggles.
        var paragraphs = _gutterEnabled ? ParagraphsToOffer(snapshot.Text, issues) : [];
        var measured = await adapter.GetBoundsAsync(drawn.Select(i => i.Range).Concat(paragraphs.Select(p => p.Range)).ToList(), snapshot.Text);
        var bounds = measured.Take(drawn.Count).ToList();
        var paragraphBounds = measured.Skip(drawn.Count).ToList();
        _compatibility?.Layout(adapter.Context, bounds.Count(b => b.Any(r => !r.IsEmpty)), drawn.Count);

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

        // Per issue, so each visible squiggle remembers its issue for hover hit-testing (screen pixels).
        var spans = new List<SquiggleSpan>();
        var regions = new List<(TextIssue, TextBounds)>();
        for (int i = 0; i < drawn.Count; i++)
        {
            foreach (var span in OverlayLayout.Layout(surface.Value, [(drawn[i].Category, bounds[i])]))
            {
                spans.Add(span);
                regions.Add((drawn[i], span.Rect with { X = span.Rect.X + surface.Value.Left, Y = span.Rect.Y + surface.Value.Top }));
            }
        }
        if (spans.Count == 0)
        {
            Hide();
            return;
        }

        _window ??= new OverlayWindow();
        _window.ShowAt(surface.Value, spans, _root);
        _hover.SetRegions(_root, regions);
        ShowGutter(surface.Value, paragraphs, paragraphBounds, snapshot.Version);
        _perf?.Record("overlay", sw.Elapsed);

        int anchorIndex = bounds.ToList().FindIndex(b => b.Count > 0);
        _anchor = anchorIndex >= 0 ? (drawn[anchorIndex].Range, bounds[anchorIndex][0]) : null;

        if (spans.Count != _lastSpanCount)
        {
            _lastSpanCount = spans.Count;
            int unplaced = bounds.Count(b => b.Count == 0);
            _logger.LogDebug("Overlay: {Spans} squiggle(s) for {Issues} issue(s) ({Unplaced} off-screen or unverified) in {Ms:F0} ms",
                spans.Count, drawn.Count, unplaced, sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Detects scrolling: re-measures the anchor word and hides squiggles while it moves.</summary>
    private async void CheckAnchor()
    {
        if (_anchor is not { } anchor || _anchorCheckRunning || _layoutRunning || _moving) return;

        // Only measure while the target is in front, like the layout pass. Besides being pointless
        // otherwise, querying a Win32 edit's text geometry through UIA pulls focus back to it — which
        // closed the suggestion popup the moment it opened.
        if (GetAncestor(GetForegroundWindow(), GA_ROOT) != _root) return;

        var adapter = _adapter;
        var snapshot = _document.Current;
        if (adapter is null || snapshot is null || _issues?.SnapshotVersion != snapshot.Version) return;

        _anchorCheckRunning = true;
        try
        {
            var rects = (await adapter.GetBoundsAsync([anchor.Range], snapshot.Text))[0];
            if (!ReferenceEquals(adapter, _adapter) || _anchor is null || _document.Current?.Version != snapshot.Version)
                return;

            var now = rects.Count > 0 ? rects[0] : TextBounds.Empty;
            if (now != anchor.Rect)
            {
                // Moving: remember where it is now and keep squiggles hidden until it stops.
                _anchor = (anchor.Range, now);
                if (!_scrolling)
                {
                    _scrolling = true;
                    _window?.HideOverlay();
                    _hover.Clear(disarm: false);
                    HideGutter();
                }
            }
            else if (_scrolling)
            {
                _scrolling = false;
                RequestLayout();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Anchor check failed");
        }
        finally
        {
            _anchorCheckRunning = false;
        }
    }

    /// <summary>Paragraphs holding at least <see cref="GutterMinChanges"/> fixes, with the count and most severe category.</summary>
    private static List<(TextRange Range, int Changes, IssueCategory Category)> ParagraphsToOffer(string text, IssueSet issues)
    {
        var result = new List<(TextRange, int, IssueCategory)>();
        foreach (var range in issues.Issues.Select(i => CompositeFix.ParagraphAt(text, i.StartOffset)).Distinct())
        {
            var fix = CompositeFix.Build(text, issues.SnapshotVersion, issues.Issues, range, FixScope.Paragraph);
            if (fix.Edits.Count < GutterMinChanges) continue;
            result.Add((range, fix.Edits.Count, fix.Edits.Select(e => e.Category).MinBy(CategoryPriority)));
            if (result.Count == MaxGutterPills) break;
        }
        return result;
    }

    private void ShowGutter(TextBounds surface, List<(TextRange Range, int Changes, IssueCategory Category)> paragraphs,
        List<IReadOnlyList<TextBounds>> lines, long version)
    {
        double scale = System.Windows.Media.VisualTreeHelper.GetDpi(_window!).DpiScaleX;
        int shown = 0;
        for (int i = 0; i < paragraphs.Count; i++)
        {
            if (GutterLayout.Place(lines[i], surface, scale) is not { } rect || GutterLayout.Extent(lines[i], surface) is not { } extent)
                continue;
            if (shown == _gutterPills.Count)
            {
                var created = new GutterPill();
                created.Clicked += () => OnGutterClicked(created);
                _gutterPills.Add(created);
            }
            var pill = _gutterPills[shown++];
            pill.Tag = new ParagraphFixRequest(paragraphs[i].Range, version, extent);
            pill.ShowAt(rect, SquiggleLayer.ColorFor(paragraphs[i].Category), $"Fix this paragraph ({paragraphs[i].Changes} changes)", _window!.Handle);
        }
        for (int i = shown; i < _gutterPills.Count; i++)
            _gutterPills[i].HidePill();
    }

    private void OnGutterClicked(GutterPill pill)
    {
        if (pill.Tag is not ParagraphFixRequest request || _document.Current?.Version != request.SnapshotVersion) return;
        ParagraphFixRequested?.Invoke(request);
    }

    private void HideGutter()
    {
        foreach (var pill in _gutterPills) pill.HidePill();
    }

    private static int CategoryPriority(IssueCategory category) => category switch
    {
        IssueCategory.Spelling => 0,
        IssueCategory.Grammar => 1,
        IssueCategory.Punctuation => 2,
        IssueCategory.Style => 3,
        _ => 4,
    };

    private void Hide()
    {
        _window?.HideOverlay();
        _hover.Clear(disarm: false);
        HideGutter();
        _lastSpanCount = -1;
    }

    public void Dispose()
    {
        _refresh.Stop();
        _settle.Stop();
        _anchorTimer.Stop();
        _hover.Dispose();
        foreach (var pill in _gutterPills) pill.Close();
        _window?.Close();
    }

    private const uint GA_ROOT = 2;
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
