using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Redline.Core.Geometry;
using Redline.Core.Models;

namespace Redline.Annotations;

/// <summary>
/// The small quick-fix pill shown when the pointer rests on a squiggle: [● top suggestion | ⋯].
/// Never activates — clicking it leaves the target app in front with its caret and focus intact,
/// which is also what the replacement engine requires before it types.
/// </summary>
internal sealed class HoverPill : Window
{
    private const int MaxSuggestionChars = 32;

    private readonly Ellipse _dot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _suggestion = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _applyPart;
    private readonly Border _divider;
    private readonly Border _morePart;
    private readonly TextBlock _moreText = new() { Text = "⋯", FontWeight = FontWeights.Bold };
    private readonly Border _frame;
    private FlyoutPalette? _palette;
    private IntPtr _hwnd;

    public HoverPill()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = -32000;
        Top = -32000;
        Title = "Redline quick fix";
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;

        _applyPart = Part(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _dot, new Border { Width = 6 }, _suggestion },
        }, new CornerRadius(10, 0, 0, 10), () => ApplyClicked?.Invoke());
        _divider = new Border { Width = 1, Margin = new Thickness(0, 4, 0, 4) };
        _morePart = Part(_moreText, new CornerRadius(0, 10, 10, 0), () => MoreClicked?.Invoke());
        _morePart.ToolTip = "More options";

        _frame = new Border
        {
            Margin = new Thickness(4, 2, 4, 6), // room for the shadow
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1.5, Opacity = 0.25 },
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _applyPart, _divider, _morePart } },
        };
        Content = _frame;
        ApplyPalette(SystemTheme.Current);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
            HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        };
    }

    /// <summary>The suggestion part was clicked.</summary>
    public event Action? ApplyClicked;

    /// <summary>The "⋯" part was clicked (or the pill, when there is no suggestion to apply).</summary>
    public event Action? MoreClicked;

    public IntPtr Handle => _hwnd;

    public bool IsShown => _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd);

    /// <summary>Screen rectangle in physical pixels (empty while hidden).</summary>
    public TextBounds ScreenRect =>
        _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd) && GetWindowRect(_hwnd, out var r)
            ? new TextBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)
            : TextBounds.Empty;

    /// <summary>Shows the pill for <paramref name="issue"/> beside <paramref name="word"/> (physical pixels).</summary>
    public void ShowFor(TextIssue issue, TextBounds word, Color color)
    {
        ApplyPalette(SystemTheme.Current); // follows light/dark changes between shows
        _dot.Fill = new SolidColorBrush(color);
        var top = issue.Suggestions.FirstOrDefault();
        bool canApply = top is not null;
        _suggestion.Text = top switch
        {
            null => issue.Category.ToString(),
            "" => "Remove",
            _ when top.Length > MaxSuggestionChars => top[..(MaxSuggestionChars - 1)] + "…",
            _ => top,
        };
        _suggestion.FontStyle = top == "" ? FontStyles.Italic : FontStyles.Normal;
        _applyPart.ToolTip = canApply ? (top == "" ? "Remove it" : "Replace with this") : "Show suggestions";
        _applyTarget = canApply ? Target.Apply : Target.More;

        if (_hwnd == IntPtr.Zero)
            Show(); // creates the HWND off-screen without activating; SetWindowPos below shows it after a hide
        UpdateLayout(); // SizeToContent resizes the window for the new text

        GetWindowRect(_hwnd, out var size);
        int width = size.Right - size.Left, height = size.Bottom - size.Top;
        var work = WorkAreaAt(word);
        var (x, y) = HoverLayout.PlacePill(word, width, height, work);
        SetWindowPos(_hwnd, HWND_TOPMOST, (int)Math.Round(x), (int)Math.Round(y), 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void HidePill()
    {
        if (_hwnd != IntPtr.Zero && IsWindowVisible(_hwnd))
            ShowWindow(_hwnd, SW_HIDE);
    }

    private void ApplyPalette(FlyoutPalette palette)
    {
        if (ReferenceEquals(palette, _palette)) return;
        _palette = palette;
        _frame.Background = palette.Background;
        _frame.BorderBrush = palette.Border;
        _divider.Background = palette.Divider;
        _suggestion.Foreground = palette.Text;
        _moreText.Foreground = palette.SecondaryText;
        _applyPart.Background = Brushes.Transparent;
        _morePart.Background = Brushes.Transparent;
    }

    private enum Target { Apply, More }
    private Target _applyTarget;

    private Border Part(UIElement content, CornerRadius corners, Action click)
    {
        var part = new Border
        {
            Padding = new Thickness(10, 3, 10, 4),
            CornerRadius = corners,
            Background = Brushes.Transparent, // hit-testable
            Cursor = Cursors.Hand,
            Child = content,
        };
        part.MouseEnter += (_, _) => part.Background = _palette?.Hover ?? Brushes.Transparent;
        part.MouseLeave += (_, _) => part.Background = Brushes.Transparent;
        part.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (ReferenceEquals(part, _applyPart) && _applyTarget == Target.More)
                MoreClicked?.Invoke();
            else
                click();
        };
        return part;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE); // clicks must not pull focus from the target
        }
        return IntPtr.Zero;
    }

    private static TextBounds WorkAreaAt(TextBounds word)
    {
        var monitor = MonitorFromPoint(new POINT { X = (int)word.Left, Y = (int)word.Top }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return new TextBounds(-32000, -32000, 64000, 64000);
        var w = info.rcWork;
        return new TextBounds(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int SW_HIDE = 0;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
