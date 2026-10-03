namespace Redline.Core.Models;

/// <summary>
/// The outcome of an attempt to modify text in a target surface.
/// </summary>
public record ReplaceResult
{
    public required bool Success { get; init; }
    public required string Method { get; init; }
    public required string OriginalText { get; init; }
    public required string ReplacementText { get; init; }
    public string? ResultText { get; init; }
    public bool VerifiedCorrect { get; init; }
    public double DurationMs { get; init; }
    public string? Error { get; init; }
}
