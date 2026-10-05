using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Redline.Core.Geometry;
using Redline.Core.Models;
using Xunit;

namespace Redline.Annotations.Tests;

/// <summary>
/// Smoke tests for the overlay: squiggles really render, and the overlay window lands directly above
/// its target in the z-order (topmost targets included). Test windows sit off-screen and never activate.
/// </summary>
public class OverlaySmokeTests
{
    /// <summary>WPF needs an STA thread; xunit's aren't.</summary>
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void SquiggleLayer_DrawsInTheCategoryColour_AlongTheBottomOfTheLine()
    {
        OnSta(() =>
        {
            var layer = new SquiggleLayer();
            layer.Update([new SquiggleSpan(new TextBounds(10, 10, 80, 20), IssueCategory.Spelling)], scale: 1.0);
            layer.Measure(new Size(120, 40));
            layer.Arrange(new Rect(0, 0, 120, 40));

            var bitmap = new RenderTargetBitmap(120, 40, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(layer);
            var pixels = new byte[120 * 40 * 4];
            bitmap.CopyPixels(pixels, 120 * 4, 0);

            bool InkAt(int x, int y) => pixels[(y * 120 + x) * 4 + 3] > 0; // alpha
            var red = SquiggleLayer.ColorFor(IssueCategory.Spelling);

            var inkRows = Enumerable.Range(0, 40).Where(y => Enumerable.Range(0, 120).Any(x => InkAt(x, y))).ToList();
            Assert.NotEmpty(inkRows);
            Assert.All(inkRows, y => Assert.InRange(y, 24, 30)); // the bottom few pixels of the 10..30 line box
            Assert.False(Enumerable.Range(0, 40).Any(y => InkAt(5, y) || InkAt(100, y)), "ink outside the span");

            // The strongest pixel is the spelling red (premultiplied BGRA).
            int best = Enumerable.Range(0, pixels.Length / 4).MaxBy(i => pixels[i * 4 + 3]);
            Assert.True(pixels[best * 4 + 2] > pixels[best * 4] + 60, "expected a red squiggle");
            Assert.Equal(0xE0, red.R);
        });
    }

    [Fact]
    public void Overlay_SitsDirectlyAboveItsTarget_AndFollowsTopmostTargets()
    {
        OnSta(() =>
        {
            var target = new Window
            {
                Left = -20000, Top = -20000, Width = 200, Height = 100, ShowActivated = false,
                ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow, Title = "Redline overlay test target",
            };
            target.Show();
            var targetHwnd = new WindowInteropHelper(target).Handle;
            var overlay = new OverlayWindow();
            try
            {
                var rect = new TextBounds(-20000, -20000, 200, 100);
                SquiggleSpan[] spans = [new(new TextBounds(10, 10, 50, 20), IssueCategory.Grammar)];

                overlay.ShowAt(rect, spans, targetHwnd);
                var overlayHwnd = new WindowInteropHelper(overlay).Handle;
                Assert.True(IsAbove(overlayHwnd, targetHwnd), "overlay below its target");
                Assert.False(IsTopmost(overlayHwnd));

                target.Topmost = true; // "always on top" target: the overlay must not end up below it
                overlay.ShowAt(rect, spans, targetHwnd);
                Assert.True(IsTopmost(overlayHwnd));
                Assert.True(IsAbove(overlayHwnd, targetHwnd), "overlay below its target");

                target.Topmost = false; // and back out of the topmost band afterwards
                overlay.ShowAt(rect, spans, targetHwnd);
                Assert.False(IsTopmost(overlayHwnd));
                Assert.True(IsAbove(overlayHwnd, targetHwnd), "overlay below its target");

                Assert.True(GetWindowRect(overlayHwnd, out var r));
                Assert.Equal((-20000, -20000, 200, 100), (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
            }
            finally
            {
                overlay.Close();
                target.Close();
            }
        });
    }

    [Fact]
    public void FlyoutPalettes_DifferBetweenLightAndDark()
    {
        Assert.False(FlyoutPalette.Light.IsDark);
        Assert.True(FlyoutPalette.Dark.IsDark);
        Assert.NotEqual(((SolidColorBrush)FlyoutPalette.Light.Background).Color, ((SolidColorBrush)FlyoutPalette.Dark.Background).Color);
        Assert.Same(SystemTheme.IsDark ? FlyoutPalette.Dark : FlyoutPalette.Light, SystemTheme.Current);
    }

    private const uint GW_HWNDPREV = 3;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008;

    /// <summary>
    /// True when <paramref name="upper"/> is above <paramref name="lower"/> in the z-order. The overlay goes directly
    /// above its target, but a window appearing elsewhere on the desktop meanwhile may land in between.
    /// </summary>
    private static bool IsAbove(IntPtr upper, IntPtr lower)
    {
        for (var h = GetWindow(lower, GW_HWNDPREV); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDPREV))
            if (h == upper) return true;
        return false;
    }

    private static bool IsTopmost(IntPtr hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}

public class GutterStickyTests
{
    [Theory]
    [InlineData(10, 2, 0, 3, 13)]   // typed before the paragraph: shifted
    [InlineData(10, 10, 0, 3, 13)]  // typed at its start: still the same paragraph
    [InlineData(10, 15, 0, 3, 10)]  // typed inside it: unchanged
    [InlineData(10, 4, 2, 0, 8)]    // deleted before it: shifted back
    [InlineData(10, 8, 5, 0, 8)]    // deletion swallowed the start: begins where the deletion did
    public void ShiftAnchor_FollowsEdits(int anchor, int start, int oldLength, int newLength, int expected)
    {
        Assert.Equal(expected, Redline.Annotations.OverlayManager.ShiftAnchor(anchor, new Redline.Core.Models.TextChange(start, oldLength, newLength)));
    }
}
