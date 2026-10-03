namespace Redline.Core.Models;

/// <summary>
/// Capability flags discovered on an active editable text surface.
/// </summary>
public record TextSurfaceCapabilities
{
    public bool CanReadText { get; init; }
    public bool CanGetCaretPosition { get; init; }
    public bool CanGetSelection { get; init; }
    public bool CanGetBoundingRectangles { get; init; }
    public bool CanDetectChanges { get; init; }
    public bool CanReplaceText { get; init; }
    public IReadOnlyList<string> SupportedPatterns { get; init; } = Array.Empty<string>();
}
