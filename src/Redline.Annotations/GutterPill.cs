using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Redline.Core.Geometry;
using Redline.Core.Models;

namespace Redline.Annotations;

/// <summary>
/// A thin vertical bar in the left gutter of a paragraph that has several fixes; clicking it opens the
/// paragraph fix. The window is a clickable strip (<see cref="GutterLayout.HitWidth"/>) around the bar.
/// Never activates, like the hover pill: the click leaves the target in front until the fix popup opens.
/// </summary>
internal sealed class GutterPill : Window
{
    private readonly Border _bar = new()
    {
        Width = GutterLayout.BarWidth,
        CornerRadius = new CornerRadius(GutterLayout.BarWidth / 2),
        HorizontalAlignment = HorizontalAlignment.Center,
        Opacity = RestOpacity,
    };
    private const double RestOpacity = 0.55;
    private IntPtr _hwnd;

    public GutterPill()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = false;
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;
        Title = "Redline paragraph fix";
        Cursor = Cursors.Hand;

        // Nearly transparent but hit-testable: a layered window lets clicks through fully transparent pixels.
        var strip = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Children = { _bar } };
        strip.MouseEnter += (_, _) => { _bar.Opacity = 1; _bar.Width = GutterLayout.BarWidth + 2; };
        strip.MouseLeave += (_, _) => { _bar.Opacity = RestOpacity; _bar.Width = GutterLayout.BarWidth; };
        strip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Clicked?.Invoke();
        };
        Content = strip;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
            HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        };
    }

    public event Action? Clicked;

    /// <summary>
    /// Shows the strip at <paramref name="rect"/> (physical pixels), directly below <paramref name="insertAfter"/>
    /// in the z-order (the overlay, which sits just above the target).
    /// </summary>
    public void ShowAt(TextBounds rect, Color color, string tooltip, IntPtr insertAfter)
    {
        if (_hwnd == IntPtr.Zero)
            Show(); // creates the HWND off-screen without activating
        _bar.Background = new SolidColorBrush(color);
        ToolTip = tooltip;
        SetWindowPos(_hwnd, insertAfter, (int)rect.X, (int)rect.Y, Math.Max(1, (int)rect.Width), Math.Max(1, (int)rect.Height),
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void HidePill()
    {
        if (_hwnd != IntPtr.Zero && IsWindowVisible(_hwnd))
            ShowWindow(_hwnd, SW_HIDE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int SW_HIDE = 0;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
}
