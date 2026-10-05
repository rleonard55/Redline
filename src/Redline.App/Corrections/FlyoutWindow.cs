using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Redline.Annotations;
using Redline.Core.Models;

namespace Redline.App.Corrections;

/// <summary>Placement and theming shared by the suggestion and fix popups.</summary>
internal static class FlyoutWindow
{
    /// <summary>Sets the <c>Flyout.*</c> brushes the popups' XAML binds to.</summary>
    public static void ApplyPalette(ResourceDictionary resources, FlyoutPalette palette)
    {
        resources["Flyout.Background"] = palette.Background;
        resources["Flyout.Border"] = palette.Border;
        resources["Flyout.Divider"] = palette.Divider;
        resources["Flyout.Text"] = palette.Text;
        resources["Flyout.SecondaryText"] = palette.SecondaryText;
        resources["Flyout.MutedText"] = palette.MutedText;
        resources["Flyout.Hover"] = palette.Hover;
        resources["Flyout.KeyboardFocus"] = palette.KeyboardFocus;
    }

    /// <summary>
    /// Moves <paramref name="window"/> (shown, measured) just below <paramref name="anchor"/> (physical screen
    /// pixels), or under the mouse pointer when there's no anchor; kept on the anchor's monitor, flipped above
    /// the text when there's no room below. The windows have an 8 px shadow margin around their border.
    /// </summary>
    public static void Place(Window window, TextBounds? anchor)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var dpi = VisualTreeHelper.GetDpi(window);
        int width = (int)Math.Ceiling((window.ActualWidth > 0 ? window.ActualWidth : 240) * dpi.DpiScaleX);
        int height = (int)Math.Ceiling((window.ActualHeight > 0 ? window.ActualHeight : 100) * dpi.DpiScaleY);
        int margin = (int)(8 * dpi.DpiScaleX);

        int x, y, anchorTop;
        if (anchor is { } a)
        {
            x = (int)a.Left - margin;
            y = (int)a.Bottom + 2 - margin;
            anchorTop = (int)a.Top;
        }
        else
        {
            GetCursorPos(out var p);
            (x, y, anchorTop) = (p.X, p.Y + 16, p.Y);
        }

        var monitor = MonitorFromPoint(new POINT { X = x + margin, Y = anchorTop }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            var work = info.rcWork;
            if (y + height > work.Bottom) y = anchorTop - height + margin - 2;
            x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
            y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        }

        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
