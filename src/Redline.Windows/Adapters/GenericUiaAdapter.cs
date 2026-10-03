using System.Windows.Automation;
using System.Windows.Automation.Text;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Windows.Automation;

namespace Redline.Windows.Adapters;

/// <summary>
/// Works with any control exposing UIA TextPattern or ValuePattern. Per Phase 0 this covers
/// Notepad, Chrome/Edge editors (TinyMCE, CKEditor) and Word.
/// </summary>
public sealed class GenericUiaAdapter : ITextSurfaceAdapter
{
    /// <summary>Reads are capped so a 500-page Word document doesn't stall the UIA thread on every keystroke.</summary>
    public const int MaxReadChars = 200_000;

    private readonly UiaDispatcher _uia;
    private readonly AutomationElement _element;
    private readonly TextPattern? _text;
    private readonly ValuePattern? _value;

    /// <summary>Must be constructed on the UIA dispatcher thread.</summary>
    public GenericUiaAdapter(UiaDispatcher uia, AutomationElement element, ElementInfo info)
    {
        _uia = uia;
        _element = element;
        if (info.SupportsTextPattern && element.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
            _text = (TextPattern)tp;
        if (info.SupportsValuePattern && element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            _value = (ValuePattern)vp;

        var patterns = new List<string>();
        if (_text is not null) patterns.Add("Text");
        if (_value is not null) patterns.Add("Value");

        Context = new TextSurfaceContext
        {
            SurfaceId = info.SurfaceId,
            ProcessName = info.ProcessName,
            ProcessId = info.ProcessId,
            ControlType = info.ControlType,
            ClassName = info.ClassName,
            FrameworkId = info.FrameworkId,
            NativeWindowHandle = info.NativeWindowHandle,
            AdapterName = Name,
        };

        Capabilities = new TextSurfaceCapabilities
        {
            CanReadText = _text is not null || _value is not null,
            CanGetCaretPosition = _text is not null,
            CanGetSelection = _text is not null,
            CanGetBoundingRectangles = _text is not null,
            CanDetectChanges = true, // events where available, polling otherwise
            CanReplaceText = false,  // Phase 2
            SupportedPatterns = patterns,
        };
    }

    public string Name => "GenericUia";
    public TextSurfaceContext Context { get; }
    public TextSurfaceCapabilities Capabilities { get; }

    public Task<string?> ReadTextAsync(CancellationToken ct = default) => InvokeOrNull(() =>
    {
        if (_text is not null)
            return _text.DocumentRange.GetText(MaxReadChars);

        if (_value is not null)
        {
            var value = _value.Current.Value ?? string.Empty;
            return value.Length > MaxReadChars ? value[..MaxReadChars] : value;
        }

        return null;
    }, ct);

    public Task<int?> GetCaretOffsetAsync(CancellationToken ct = default) => InvokeOrNull<int?>(() =>
    {
        if (_text is null) return null;

        var selection = _text.GetSelection();
        if (selection is null || selection.Length == 0) return null;

        // Offset = length of the text between document start and the selection start.
        var prefix = _text.DocumentRange.Clone();
        prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection[0], TextPatternRangeEndpoint.Start);
        return prefix.GetText(-1)?.Length;
    }, ct);

    public async Task<IReadOnlyList<TextBounds>> GetBoundsAsync(TextRange range, CancellationToken ct = default)
    {
        var result = await InvokeOrNull<IReadOnlyList<TextBounds>>(() =>
        {
            if (_text is null || range.Start < 0 || range.Length <= 0) return null;

            // NOTE: UIA "Character" units are provider-defined. For most providers one unit is one
            // UTF-16 code unit, but some count CRLF or surrogate pairs as one. Phase 3's
            // GeometryMapper must validate this per adapter (risk R1/R8).
            var r = _text.DocumentRange.Clone();
            r.MoveEndpointByRange(TextPatternRangeEndpoint.End, r, TextPatternRangeEndpoint.Start);
            if (range.Start > 0)
                r.Move(TextUnit.Character, range.Start);
            r.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, range.Length);

            // Physical screen pixels (Redline is PerMonitorV2-aware).
            return r.GetBoundingRectangles()
                .Select(rect => new TextBounds(rect.X, rect.Y, rect.Width, rect.Height))
                .Where(b => !b.IsEmpty)
                .ToList();
        }, ct).ConfigureAwait(false);

        return result ?? Array.Empty<TextBounds>();
    }

    private async Task<T?> InvokeOrNull<T>(Func<T?> func, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return await _uia.InvokeAsync(func).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ElementNotAvailableException)
        {
            return default;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Target process hung or exited mid-call.
            return default;
        }
    }

    public void Dispose()
    {
        // Pattern objects are RCWs owned by the managed UIA client; nothing to release explicitly.
    }
}
