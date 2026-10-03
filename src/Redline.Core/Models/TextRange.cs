namespace Redline.Core.Models;

/// <summary>
/// Represents a range within a text document identified by 0-based character start and length.
/// </summary>
public readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;

    public bool IsEmpty => Length == 0;

    public bool Contains(int index) => index >= Start && index < End;

    public bool IntersectsWith(TextRange other) =>
        Math.Max(Start, other.Start) < Math.Min(End, other.End);

    public static TextRange Empty => new(0, 0);

    public override string ToString() => $"[{Start}..{End}) (len: {Length})";
}
