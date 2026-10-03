using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Redline.Core.Models;

namespace Redline.CompatibilityHarness.Ui;

public partial class ProbeOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    private readonly DispatcherTimer _autoHideTimer;

    public ProbeOverlayWindow(IEnumerable<TextBounds> bounds, string labelText, bool allReasonable)
    {
        InitializeComponent();

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        RenderBounds(bounds, labelText, allReasonable);

        _autoHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _autoHideTimer.Tick += (s, e) =>
        {
            _autoHideTimer.Stop();
            Close();
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Set WS_EX_TRANSPARENT | WS_EX_LAYERED for true click-through
        var hwnd = new WindowInteropHelper(this).Handle;
        int currentExStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, currentExStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT);

        _autoHideTimer.Start();
    }

    private void RenderBounds(IEnumerable<TextBounds> bounds, string labelText, bool allReasonable)
    {
        OverlayCanvas.Children.Clear();

        // Convert virtual screen coordinates to window-local coordinates
        double screenOffsetX = SystemParameters.VirtualScreenLeft;
        double screenOffsetY = SystemParameters.VirtualScreenTop;

        var strokeBrush = allReasonable
            ? new SolidColorBrush(Color.FromArgb(220, 0, 220, 100))
            : new SolidColorBrush(Color.FromArgb(220, 240, 50, 50));

        var fillBrush = allReasonable
            ? new SolidColorBrush(Color.FromArgb(50, 0, 220, 100))
            : new SolidColorBrush(Color.FromArgb(50, 240, 50, 50));

        foreach (var b in bounds)
        {
            var rect = new Rectangle
            {
                Width = Math.Max(b.Width, 2),
                Height = Math.Max(b.Height, 2),
                Stroke = strokeBrush,
                StrokeThickness = 2,
                Fill = fillBrush
            };

            Canvas.SetLeft(rect, b.X - screenOffsetX);
            Canvas.SetTop(rect, b.Y - screenOffsetY);
            OverlayCanvas.Children.Add(rect);

            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 2, 4, 2),
                Child = new TextBlock
                {
                    Text = $"\"{labelText}\" ({b.Width:F0}x{b.Height:F0})",
                    Foreground = Brushes.White,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold
                }
            };

            Canvas.SetLeft(label, b.X - screenOffsetX);
            Canvas.SetTop(label, Math.Max(0, b.Y - screenOffsetY - 22));
            OverlayCanvas.Children.Add(label);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        _autoHideTimer.Stop();
        Close();
    }
}
