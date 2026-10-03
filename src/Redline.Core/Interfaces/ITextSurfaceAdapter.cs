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

    // --- Editing (used by the replacement engine) ---

    /// <summary>True if this surface currently holds keyboard focus.</summary>
    Task<bool> HasKeyboardFocusAsync(CancellationToken ct = default);

    /// <summary>Asks the surface to take keyboard focus (activating its window).</summary>
    Task FocusAsync(CancellationToken ct = default);

    /// <summary>
    /// Selects <paramref name="range"/> (UTF-16 offsets into <paramref name="documentText"/>) and
    /// returns true only if the control's selection then reads exactly as that substring.
    /// </summary>
    Task<bool> SelectAsync(TextRange range, string documentText, CancellationToken ct = default);

    /// <summary>
    /// Replaces the control's entire value, but only if it still equals <paramref name="expectedCurrent"/>.
    /// Returns false if unsupported or the value had changed.
    /// </summary>
    Task<bool> SetValueAsync(string expectedCurrent, string newValue, CancellationToken ct = default);
}
