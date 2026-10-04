using System.Runtime.InteropServices;
using System.Windows.Threading;
using Redline.Core.Geometry;
using Redline.Core.Models;

namespace Redline.Annotations;

/// <summary>
/// Shows the quick-fix pill when the pointer rests on a squiggled word, and hides it when the
/// pointer leaves. Works purely from the rectangles the overlay already measured: no UIA queries,
/// and the overlay itself stays click-through. UI thread only.
/// </summary>
/// <remarks>
/// The pointer is polled (cheap: GetCursorPos) only while squiggles are on screen. A system-wide
/// mouse hook would add latency to every mouse move in every app, so it's avoided. Hovering only
/// counts after the pointer has moved since the text last changed, so a mouse resting on a word
/// while the user types doesn't pop the pill up.
/// </remarks>
internal sealed class HoverController : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherTimer _poll;
    private HoverPill? _pill;

    private IntPtr _root;
    private IReadOnlyList<(TextIssue Issue, TextBounds Rect)> _regions = [];
    private TextBounds[] _rects = [];

    private POINT _lastCursor;
    private bool _armed;
    private int _candidate = -1;
    private DateTime _candidateSince;
    private (TextIssue Issue, TextBounds Word)? _shown;
    private DateTime? _outsideSince;

    public HoverController(Dispatcher ui)
    {
        _poll = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = PollInterval };
        _poll.Tick += (_, _) => Poll();
        GetCursorPos(out _lastCursor);
    }

    /// <summary>Off = never show the pill.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) Clear(disarm: false);
        }
    }
    private bool _enabled = true;

    /// <summary>The pill's suggestion was clicked: apply this issue's first suggestion.</summary>
    public event Action<TextIssue>? ApplyRequested;

    /// <summary>The pill's "⋯" was clicked: show the full suggestion popup for this issue.</summary>
    public event Action<TextIssue>? MoreRequested;

    /// <summary>
    /// Squiggled words now on screen (physical pixels) over the target's top-level window <paramref name="root"/>.
    /// Called after every overlay layout; a pill whose issue is gone (or has moved) is hidden.
    /// </summary>
    public void SetRegions(IntPtr root, IReadOnlyList<(TextIssue Issue, TextBounds Rect)> regions)
    {
        if (!_enabled) return;
        var rects = regions.Select(r => r.Rect).ToArray();
        // The overlay re-lays out every 400 ms; only restart the hover delay if something moved.
        if (root != _root || !rects.SequenceEqual(_rects)) _candidate = -1;
        _root = root;
        _regions = regions;
        _rects = rects;

        if (_shown is { } shown && !regions.Any(r => r.Issue == shown.Issue && r.Rect == shown.Word))
            HidePill();

        if (regions.Count > 0) _poll.Start();
        else if (_shown is null) _poll.Stop();
    }

    /// <summary>Squiggles hidden or invalid. <paramref name="disarm"/>: the text changed, so wait for the pointer to move.</summary>
    public void Clear(bool disarm)
    {
        _regions = [];
        _rects = [];
        _candidate = -1;
        if (disarm) _armed = false;
        HidePill();
        _poll.Stop();
    }

    private void Poll()
    {
        if (!GetCursorPos(out var p)) return;
        if (p.X != _lastCursor.X || p.Y != _lastCursor.Y)
        {
            _armed = true;
            _lastCursor = p;
        }

        if (_shown is { } shown)
        {
            KeepOrHide(shown, p);
            return;
        }

        if (!_armed || _regions.Count == 0 || ButtonsDown() || !PointerOverTarget(p) || !TargetInFront())
        {
            _candidate = -1;
            return;
        }

        int hit = HoverLayout.HitTest(_rects, p.X, p.Y);
        if (hit != _candidate)
        {
            _candidate = hit;
            _candidateSince = DateTime.UtcNow;
            return;
        }
        if (hit >= 0 && DateTime.UtcNow - _candidateSince >= ShowDelay)
            ShowPill(_regions[hit]);
    }

    private void KeepOrHide((TextIssue Issue, TextBounds Word) shown, POINT p)
    {
        var pillRect = _pill?.ScreenRect ?? TextBounds.Empty;
        if (HoverLayout.KeepOpenZone(shown.Word, pillRect).Contains(p.X, p.Y) && TargetInFront())
        {
            _outsideSince = null;
            return;
        }

        // Moving straight onto another flagged word switches without waiting for the hide delay.
        int hit = PointerOverTarget(p) ? HoverLayout.HitTest(_rects, p.X, p.Y) : -1;
        if (hit >= 0 && _regions[hit].Issue != shown.Issue)
        {
            _outsideSince = null;
            HidePill();
            _candidate = hit;
            _candidateSince = DateTime.UtcNow;
            return;
        }

        _outsideSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _outsideSince >= HideDelay || !TargetInFront())
            HidePill();
    }

    private void ShowPill((TextIssue Issue, TextBounds Rect) region)
    {
        if (_pill is null)
        {
            _pill = new HoverPill();
            _pill.ApplyClicked += () => Clicked(ApplyRequested);
            _pill.MoreClicked += () => Clicked(MoreRequested);
        }
        _pill.ShowFor(region.Issue, region.Rect, SquiggleLayer.ColorFor(region.Issue.Category));
        _shown = (region.Issue, region.Rect);
        _outsideSince = null;
        _candidate = -1;
    }

    private void Clicked(Action<TextIssue>? handler)
    {
        if (_shown is not { } shown) return;
        HidePill();
        _armed = false; // don't pop straight back up under the pointer after the fix
        handler?.Invoke(shown.Issue);
    }

    private void HidePill()
    {
        _pill?.HidePill();
        _shown = null;
        _outsideSince = null;
        if (_regions.Count == 0) _poll.Stop();
    }

    /// <summary>The pointer is over the target window (or the pill), not over something covering it.</summary>
    private bool PointerOverTarget(POINT p)
    {
        var under = WindowFromPoint(p);
        if (_pill is not null && under == _pill.Handle) return true;
        return _root != IntPtr.Zero && GetAncestor(under, GA_ROOT) == _root;
    }

    private bool TargetInFront() => _root != IntPtr.Zero && GetAncestor(GetForegroundWindow(), GA_ROOT) == _root;

    /// <summary>Dragging a selection over a word shouldn't summon the pill.</summary>
    private static bool ButtonsDown() =>
        (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

    public void Dispose()
    {
        _poll.Stop();
        _pill?.Close();
    }

    private const uint GA_ROOT = 2;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
}
