namespace Redline.Core.Models;

/// <summary>
/// An immutable snapshot of text extracted from a target surface at a specific point in time.
/// </summary>
public record TextSnapshot(
    string Text,
    long Version,
    DateTimeOffset Timestamp)
{
    public int Length => Text.Length;

    public static TextSnapshot Empty => new(string.Empty, 0, DateTimeOffset.UtcNow);

    public string GetSubstring(TextRange range)
    {
        if (range.Start < 0 || range.Start > Text.Length) return string.Empty;
        var validLen = Math.Min(range.Length, Text.Length - range.Start);
        return validLen <= 0 ? string.Empty : Text.Substring(range.Start, validLen);
    }
}
