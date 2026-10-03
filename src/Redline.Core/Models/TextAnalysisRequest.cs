namespace Redline.Core.Models;

/// <summary>
/// A slice of a snapshot handed to an analyzer. Analyzers report issue offsets in
/// document coordinates: offset-within-<see cref="Text"/> + <see cref="ContextOffset"/>.
/// </summary>
public record TextAnalysisRequest
{
    public required string Text { get; init; }
    public int ContextOffset { get; init; }
    public long SnapshotVersion { get; init; }
    public string Language { get; init; } = "en-US";
}
