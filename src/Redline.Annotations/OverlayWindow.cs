using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Redline.Core.Geometry;
using Redline.Core.Models;

namespace Redline.Annotations;

/// <summary>
/// Transparent, click-through window that sits directly above the target's top-level window and
/// draws squiggles. Never activates, never takes input, hidden from Alt+Tab and the taskbar.
/// </summary>
/// <remarks>
/// Z-order: placed immediately above the target window rather than topmost. Anything that later
/// covers the target (another app, the target's own menus and dropdowns) covers the squiggles too.
/// A topmost ("always on top") target gets a topmost overlay, again directly above it.
/// Deliberately not made an owned window of the target: cross-process ownership attaches the two
/// processes' input queues, so a hung target could hang Redline.
/// </remarks>
internal sealed class OverlayWindow : Window
{
    private readonly SquiggleLayer _layer = new();
    private IntPtr _hwnd;

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = false;
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;
        Title = "Redline overlay";
        Content = _layer;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
        };
    }

    /// <summary>Shows squiggles over <paramref name="screenRect"/> (physical pixels), just above <paramref name="target"/>.</summary>
    public void ShowAt(TextBounds screenRect, IReadOnlyList<SquiggleSpan> spans, IntPtr target)
    {
        if (_hwnd == IntPtr.Zero)
        {
            Show(); // creates the HWND off-screen; ShowActivated=false keeps focus where it is
        }

        int x = (int)Math.Round(screenRect.Left), y = (int)Math.Round(screenRect.Top);
        int w = Math.Max(1, (int)Math.Round(screenRect.Width)), h = Math.Max(1, (int)Math.Round(screenRect.Height));
        var insertAfter = InsertAfterFor(target, out bool keepZOrder);
        uint flags = SWP_NOACTIVATE | SWP_SHOWWINDOW | (keepZOrder ? SWP_NOZORDER : 0);
        SetWindowPos(_hwnd, insertAfter, x, y, w, h, flags);

        // Moving onto a monitor with another scale sends WM_DPICHANGED during the call, and WPF answers
        // by resizing the window to its own suggested rectangle. Put it back: this time no DPI changes.
        if (GetWindowRect(_hwnd, out var actual) && (actual.Left != x || actual.Top != y || actual.Right - actual.Left != w || actual.Bottom - actual.Top != h))
            SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, SWP_NOACTIVATE | SWP_NOZORDER);

        // Read DPI after positioning: moving onto another monitor may have changed it.
        _layer.Update(spans, VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    public void HideOverlay()
    {
        if (_hwnd == IntPtr.Zero) return;
        _layer.Update(Array.Empty<SquiggleSpan>(), 1.0);
        ShowWindow(_hwnd, SW_HIDE);
    }

    /// <summary>
    /// The window to insert after (i.e. directly below) so the overlay lands just above <paramref name="target"/>.
    /// </summary>
    private IntPtr InsertAfterFor(IntPtr target, out bool keepZOrder)
    {
        keepZOrder = false;
        bool targetTopmost = IsTopmost(target);

        // Leaving a topmost target: drop out of the topmost band first (HWND_TOP would keep us in it).
        if (!targetTopmost && IsTopmost(_hwnd))
            SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        var above = GetWindow(target, GW_HWNDPREV);
        if (above == _hwnd)
        {
            keepZOrder = true; // already in place
            return IntPtr.Zero;
        }

        if (targetTopmost)
        {
            // Everything above a topmost window is topmost too, so inserting after it makes the overlay
            // topmost and leaves it directly above the target.
            return above == IntPtr.Zero ? HWND_TOPMOST : above;
        }

        // Target is the top non-topmost window (the usual case: it's the foreground app). HWND_TOP
        // puts the overlay at the top of the non-topmost band, i.e. right above it. Never insert
        // after a topmost window: that would make the overlay topmost too.
        if (above == IntPtr.Zero || IsTopmost(above))
            return HWND_TOP;
        return above;
    }

    private static bool IsTopmost(IntPtr hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_LAYERED = 0x00080000;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const uint GW_HWNDPREV = 3;
    private const int SW_HIDE = 0;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}
