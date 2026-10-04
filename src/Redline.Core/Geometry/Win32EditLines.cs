using Redline.Core.Models;

namespace Redline.Core.Geometry;

/// <summary>
/// Repairs text rectangles from multiline classic Edit controls (Win32 and WinForms TextBox). UIA's Win32
/// proxy reports their height as the font's em height (14 pt Segoe UI: 19 px) instead of the line height
/// the control lays text out with (25 px), so the rectangle's bottom sits above the baseline and underlines
/// cross the letters. The top is right; the line height is the control font's <c>tmHeight</c>.
/// Single-line edits already report the full line height.
/// </summary>
public static class Win32EditLines
{
    public const int EsMultiline = 0x0004;

    public static bool IsMultilineEdit(string className, int style) =>
        (style & EsMultiline) != 0 &&
        (className.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
         className.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase));

    /// <summary><paramref name="rect"/> grown downward to <paramref name="lineHeight"/>; never shrunk.</summary>
    public static TextBounds WithLineHeight(TextBounds rect, int lineHeight) =>
        rect.IsEmpty || lineHeight <= rect.Height ? rect : rect with { Height = lineHeight };
}
