namespace Redline.Core.Models;

/// <summary>
/// Platform-agnostic bounding rectangle in screen physical or DIP coordinates.
/// </summary>
public readonly record struct TextBounds(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static TextBounds Empty => new(0, 0, 0, 0);

    public bool Contains(double px, double py) =>
        px >= Left && px <= Right && py >= Top && py <= Bottom;

    public override string ToString() => $"({X:F1}, {Y:F1}, {Width:F1}x{Height:F1})";
}
