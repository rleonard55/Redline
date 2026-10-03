using Redline.Core.Models;

namespace Redline.Core.Interfaces;

/// <summary>
/// Bound to one editable control. Implementations marshal to whatever thread their
/// platform API requires, so callers may invoke these from any thread.
/// </summary>
public interface ITextSurfaceAdapter : IDisposable
{
    string Name { get; }
    TextSurfaceContext Context { get; }
    TextSurfaceCapabilities Capabilities { get; }

    /// <summary>Reads the full text, or null if the surface is gone or unreadable.</summary>
    Task<string?> ReadTextAsync(CancellationToken ct = default);

    /// <summary>Caret offset in document coordinates, or null if unavailable.</summary>
    Task<int?> GetCaretOffsetAsync(CancellationToken ct = default);

    /// <summary>Screen rectangles (physical pixels) covering <paramref name="range"/>; empty if unavailable.</summary>
    Task<IReadOnlyList<TextBounds>> GetBoundsAsync(TextRange range, CancellationToken ct = default);
}
