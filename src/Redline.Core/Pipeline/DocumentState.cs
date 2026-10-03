using Redline.Core.Models;
using Redline.Core.Text;

namespace Redline.Core.Pipeline;

/// <summary>
/// Tracks text snapshots for the active surface. Versions increase monotonically across
/// surfaces, so a version alone identifies a snapshot for the lifetime of the process.
/// Thread-safe.
/// </summary>
public sealed class DocumentState
{
    private readonly object _gate = new();
    private long _lastVersion;

    public string? SurfaceId { get { lock (_gate) return _surfaceId; } }
    public TextSnapshot? Current { get { lock (_gate) return _current; } }
    public TextSnapshot? Previous { get { lock (_gate) return _previous; } }

    private string? _surfaceId;
    private TextSnapshot? _current;
    private TextSnapshot? _previous;

    /// <summary>Begins tracking a new surface (or none), discarding prior snapshots.</summary>
    public void Reset(string? surfaceId)
    {
        lock (_gate)
        {
            _surfaceId = surfaceId;
            _current = null;
            _previous = null;
        }
    }

    /// <summary>
    /// Records <paramref name="text"/> as the latest content of <paramref name="surfaceId"/>.
    /// Returns the new snapshot and its change relative to the previous one (null change for
    /// the first snapshot), or null when the text is unchanged or the surface is no longer tracked.
    /// </summary>
    public (TextSnapshot Snapshot, TextChange? Change)? Update(string surfaceId, string text)
    {
        lock (_gate)
        {
            if (!string.Equals(surfaceId, _surfaceId, StringComparison.Ordinal))
                return null;

            if (_current is not null && string.Equals(_current.Text, text, StringComparison.Ordinal))
                return null;

            TextChange? change = _current is null ? null : TextDiff.Compute(_current.Text, text);
            _previous = _current;
            _current = new TextSnapshot(text, ++_lastVersion, DateTimeOffset.UtcNow);
            return (_current, change);
        }
    }
}
