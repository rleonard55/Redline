using System.Windows.Automation;
using System.Windows.Automation.Text;
using Redline.Core.Corrections;
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
    private readonly string _runtimeId;

    /// <summary>Must be constructed on the UIA dispatcher thread.</summary>
    public GenericUiaAdapter(UiaDispatcher uia, AutomationElement element, ElementInfo info)
    {
        _uia = uia;
        _element = element;
        _runtimeId = info.RuntimeId;
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
            CanReplaceText = _text is not null || (_value is not null && !info.ValueIsReadOnly),
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
            var r = CreateRange(_text, range);
            if (r is null) return null;

            // Physical screen pixels (Redline is PerMonitorV2-aware).
            return r.GetBoundingRectangles()
                .Select(rect => new TextBounds(rect.X, rect.Y, rect.Width, rect.Height))
                .Where(b => !b.IsEmpty)
                .ToList();
        }, ct).ConfigureAwait(false);

        return result ?? Array.Empty<TextBounds>();
    }

    public async Task<bool> HasKeyboardFocusAsync(CancellationToken ct = default) =>
        await InvokeOrNull<bool?>(() =>
        {
            // Synthesized input goes to the foreground window, whatever UIA says. Require our exact
            // top-level window: Chromium can report DOM focus while another window — or even its own
            // page child window (Chrome_RenderWidgetHostHWND) — is foreground, and then keystrokes
            // never reach the page.
            var root = TopLevelWindow();
            if (root == IntPtr.Zero || GetForegroundWindow() != root)
                return false;

            // Focus may sit on a child of the attached element (e.g. Word's inner edit inside _WwG).
            var walker = TreeWalker.ControlViewWalker;
            var node = AutomationElement.FocusedElement;
            for (int depth = 0; node is not null && depth < 8; depth++, node = walker.GetParent(node))
            {
                if (string.Join(".", node.GetRuntimeId() ?? Array.Empty<int>()) == _runtimeId &&
                    node.Current.ProcessId == Context.ProcessId)
                    return true;
            }
            return false;
        }, ct).ConfigureAwait(false) ?? false;

    public Task FocusAsync(CancellationToken ct = default) => InvokeOrNull<object?>(() =>
    {
        // Bring the top-level window forward first: for Chromium content SetFocus alone only moves
        // focus within the page. Works when Redline is foreground (popup/diagnostics/hotkey).
        var root = TopLevelWindow();
        if (root != IntPtr.Zero && GetForegroundWindow() != root)
            SetForegroundWindow(root);
        _element.SetFocus();
        return null;
    }, ct);

    /// <summary>The element's top-level window: nearest ancestor with a native handle, then its root. UIA thread only.</summary>
    private IntPtr TopLevelWindow()
    {
        if (_topLevelWindow != IntPtr.Zero) return _topLevelWindow;
        var walker = TreeWalker.RawViewWalker;
        for (var node = _element; node is not null; node = walker.GetParent(node))
        {
            var handle = node.Current.NativeWindowHandle;
            if (handle != 0)
                return _topLevelWindow = GetAncestor(new IntPtr(handle), GA_ROOT);
        }
        return IntPtr.Zero;
    }

    private IntPtr _topLevelWindow;

    private const uint GA_ROOT = 2;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);


    public async Task<bool> SelectAsync(TextRange range, string documentText, CancellationToken ct = default)
    {
        if (_text is null || range.Start < 0 || range.End > documentText.Length) return false;
        var expected = documentText.Substring(range.Start, range.Length);

        // Try each plausible mapping from UTF-16 offsets to this provider's character units and
        // select the first whose text matches; only then touch the user's selection.
        bool selected = await InvokeOrNull<bool?>(() =>
        {
            foreach (var candidate in CorrectionMath.ProviderUnitCandidates(documentText, range))
            {
                var r = CreateRange(_text, candidate);
                if (r is null || r.GetText(-1) != expected)
                    continue;
                r.Select();
                return true;
            }
            return false;
        }, ct).ConfigureAwait(false) ?? false;
        if (!selected) return false;

        // Chromium applies Select() asynchronously (the renderer process handles it), so the
        // selection can read stale for a moment. Poll until it reads exactly as expected.
        var deadline = DateTime.UtcNow + SelectionSettleTimeout;
        while (true)
        {
            bool matches = await InvokeOrNull<bool?>(() =>
            {
                var selection = _text.GetSelection();
                return selection is { Length: 1 } && selection[0].GetText(-1) == expected;
            }, ct).ConfigureAwait(false) ?? false;
            if (matches) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    private static readonly TimeSpan SelectionSettleTimeout = TimeSpan.FromMilliseconds(500);

    public async Task<bool> SetValueAsync(string expectedCurrent, string newValue, CancellationToken ct = default) =>
        await InvokeOrNull<bool?>(() =>
        {
            if (_value is null || _value.Current.IsReadOnly) return false;
            if (!string.Equals(_value.Current.Value, expectedCurrent, StringComparison.Ordinal)) return false;
            _value.SetValue(newValue);
            return true;
        }, ct).ConfigureAwait(false) ?? false;

    /// <summary>
    /// A range covering <paramref name="units"/>, counted in this provider's character units from the
    /// document start, or null if the document is shorter. UIA thread only.
    /// </summary>
    private static TextPatternRange? CreateRange(TextPattern text, TextRange units)
    {
        var r = text.DocumentRange.Clone();
        r.MoveEndpointByRange(TextPatternRangeEndpoint.End, r, TextPatternRangeEndpoint.Start);
        if (units.Start > 0 && r.Move(TextUnit.Character, units.Start) != units.Start)
            return null;
        // Some providers (WPF) expand a degenerate range to one unit on Move; collapse it again so
        // the length below is measured from the start.
        r.MoveEndpointByRange(TextPatternRangeEndpoint.End, r, TextPatternRangeEndpoint.Start);
        // Don't trust the returned count here: WPF reports one unit short when the endpoint reaches
        // the document end even though the range is complete. Callers verify the range's text instead.
        if (units.Length > 0)
            r.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, units.Length);
        return r;
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
