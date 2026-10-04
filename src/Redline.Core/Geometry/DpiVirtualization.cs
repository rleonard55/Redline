using Redline.Core.Models;

namespace Redline.Core.Geometry;

/// <summary>
/// Repairs text rectangles from classic Win32 controls (Edit, RichEdit, WinForms) inside windows that
/// Windows bitmap-scales: a DPI-unaware app, or a system-aware one after the display scale changed.
/// UIA's Win32 proxy builds those rectangles from the control's screen origin (already physical) plus
/// character offsets the control reports in its own unscaled units, so every rectangle ends up pulled
/// toward the control's top-left by the scale factor. Scaling the offsets back fixes them.
/// </summary>
public static class DpiVirtualization
{
    /// <summary>
    /// Monitor DPI / window DPI when the window is virtualized (e.g. 120/96 = 1.25), otherwise 1.
    /// Zero or unknown DPIs count as not virtualized.
    /// </summary>
    public static double Scale(uint windowDpi, uint monitorDpi) =>
        windowDpi == 0 || monitorDpi == 0 || windowDpi == monitorDpi ? 1.0 : (double)monitorDpi / windowDpi;

    /// <summary>
    /// <paramref name="rect"/> with its offset from the control's client origin (physical screen pixels)
    /// multiplied by <paramref name="scale"/>, and its size too.
    /// </summary>
    public static TextBounds FromClientOrigin(TextBounds rect, double originX, double originY, double scale) =>
        scale == 1.0
            ? rect
            : new TextBounds(
                originX + (rect.Left - originX) * scale,
                originY + (rect.Top - originY) * scale,
                rect.Width * scale,
                rect.Height * scale);
}
