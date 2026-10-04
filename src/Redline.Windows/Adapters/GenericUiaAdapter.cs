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
        {
            var text = _text.DocumentRange.GetText(MaxReadChars);

            // Chromium exposes an empty input's placeholder ("Subject", "Type a message") through
            // TextPattern while ValuePattern correctly reports "". Analyzing or "correcting" a
            // placeholder would be wrong, so trust the value. Worst case if this misfires: a field
            // goes unchecked — never a bad edit.
            if (text.Length > 0 && _value is not null && Context.FrameworkId == "Chrome" && _value.Current.Value.Length == 0)
                return string.Empty;

            return text;
        }

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

    public async Task<IReadOnlyList<IReadOnlyList<TextBounds>>> GetBoundsAsync(
        IReadOnlyList<TextRange> ranges, string documentText, CancellationToken ct = default)
    {
        var result = await InvokeOrNull<IReadOnlyList<IReadOnlyList<TextBounds>>>(() =>
        {
            if (_text is null) return null;
            var all = new List<IReadOnlyList<TextBounds>>(ranges.Count);
            int driftHint = 0; // drift tends to grow monotonically through a document
            foreach (var range in ranges)
                all.Add(VerifiedBounds(_text, range, documentText, ref driftHint));
            return all;
        }, ct).ConfigureAwait(false);

        return result ?? ranges.Select(_ => (IReadOnlyList<TextBounds>)Array.Empty<TextBounds>()).ToList();
    }

    /// <summary>UIA thread only. Rectangles for the verified range; none if it can't be located exactly.</summary>
    private static IReadOnlyList<TextBounds> VerifiedBounds(TextPattern text, TextRange range, string documentText, ref int driftHint)
    {
        var r = FindRange(text, documentText, range, ref driftHint);
        if (r is null) return Array.Empty<TextBounds>();
        return r.GetBoundingRectangles()
            .Select(rect => new TextBounds(rect.X, rect.Y, rect.Width, rect.Height))
            .Where(b => !b.IsEmpty)
            .ToList();
    }

    /// <summary>
    /// UIA thread only. The provider range whose text is exactly <paramref name="range"/> of
    /// <paramref name="documentText"/>, or null. First tries the fixed unit conventions; if none fits
    /// (Chromium counts some embedded objects and paragraph breaks as more than one unit, e.g. after
    /// an email signature's images), searches nearby offsets. A drift match must also reproduce a few
    /// characters of surrounding context, so a nearby repeat of the same word can't be picked.
    /// </summary>
    private static TextPatternRange? FindRange(TextPattern text, string documentText, TextRange range, ref int driftHint)
    {
        if (range.Start < 0 || range.Length <= 0 || range.End > documentText.Length) return null;
        var expected = documentText.Substring(range.Start, range.Length);

        foreach (var candidate in CorrectionMath.ProviderUnitCandidates(documentText, range))
        {
            var r = CreateRange(text, candidate);
            if (r is not null && r.GetText(-1) == expected)
                return r;
        }

        int maxDrift = CorrectionMath.MaxPlausibleDrift(documentText, range.Start);
        if (maxDrift == 0) return null;

        var (before, after) = CorrectionMath.ContextAround(documentText, range);
        foreach (int drift in CorrectionMath.DriftOrder(driftHint, maxDrift))
        {
            if (range.Start + drift < 0) continue;
            var r = CreateRange(text, new TextRange(range.Start + drift, range.Length));
            if (r is null || r.GetText(-1) != expected || !ContextMatches(r, before, after))
                continue;
            driftHint = drift;
            return r;
        }
        return null;
    }

    /// <summary>UIA thread only. True if the text just before and after <paramref name="r"/> reads as given.</summary>
    private static bool ContextMatches(TextPatternRange r, string before, string after)
    {
        if (before.Length > 0)
        {
            var b = r.Clone();
            b.MoveEndpointByRange(TextPatternRangeEndpoint.End, b, TextPatternRangeEndpoint.Start);
            b.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -before.Length);
            if (b.GetText(-1) != before) return false;
        }
        if (after.Length > 0)
        {
            var a = r.Clone();
            a.MoveEndpointByRange(TextPatternRangeEndpoint.Start, a, TextPatternRangeEndpoint.End);
            a.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, after.Length);
            if (a.GetText(-1) != after) return false;
        }
        return true;
    }

    public Task<TextBounds?> GetSurfaceBoundsAsync(CancellationToken ct = default) => InvokeOrNull<TextBounds?>(() =>
    {
        var r = _element.Current.BoundingRectangle;
        if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return null;

        // Clip to the top-level window so a control larger than its scroll viewport (or partly
        // outside the window) doesn't extend the overlay past what's actually on screen.
        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        var root = TopLevelWindow();
        if (root != IntPtr.Zero && GetWindowRect(root, out var w))
        {
            left = Math.Max(left, w.Left);
            top = Math.Max(top, w.Top);
            right = Math.Min(right, w.Right);
            bottom = Math.Min(bottom, w.Bottom);
        }
        return right - left < 1 || bottom - top < 1 ? null : new TextBounds(left, top, right - left, bottom - top);
    }, ct);

    public async Task<long> GetTopLevelWindowAsync(CancellationToken ct = default) =>
        await InvokeOrNull<long?>(() => TopLevelWindow().ToInt64(), ct).ConfigureAwait(false) ?? 0;

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
        FocusCore();
        return null;
    }, ct);

    /// <summary>
    /// UIA thread only. SetFocus first, then make sure the top-level window is foreground: for
    /// Chromium content SetFocus only moves focus within the page, and can leave the page's child
    /// window (Chrome_RenderWidgetHostHWND) as the foreground window, where keystrokes don't reach
    /// the page. Works when Redline is foreground (popup/diagnostics/hotkey).
    /// </summary>
    private void FocusCore()
    {
        _element.SetFocus();
        var root = TopLevelWindow();
        if (root != IntPtr.Zero && GetForegroundWindow() != root)
            SetForegroundWindow(root);
    }

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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);


    public async Task<bool> SelectAsync(TextRange range, string documentText, CancellationToken ct = default)
    {
        if (_text is null || range.Start < 0 || range.End > documentText.Length) return false;
        var expected = documentText.Substring(range.Start, range.Length);

        // Locate the exact range first (see FindRange); only then touch focus and the selection.
        if (!await SelectRangeAsync(range, documentText, ct).ConfigureAwait(false))
            return false;

        // Chromium applies Select() asynchronously (the renderer process handles it), so the
        // selection can read stale for a moment. Poll until it reads as expected; if it still hasn't
        // after a while, select once more (rich editors sometimes drop the first request).
        var deadline = DateTime.UtcNow + SelectionSettleTimeout;
        var reselectAt = DateTime.UtcNow + ReselectAfter;
        bool reselected = false;

        while (true)
        {
            bool matches = await InvokeOrNull<bool?>(() => SelectionReadsAs(_text, expected), ct).ConfigureAwait(false) ?? false;
            if (matches) return true;
            if (DateTime.UtcNow >= deadline) return false;

            if (!reselected && DateTime.UtcNow >= reselectAt)
            {
                reselected = true;
                await SelectRangeAsync(range, documentText, ct).ConfigureAwait(false);
            }

            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Focuses the surface and selects the verified range. False if the range can't be located.</summary>
    private async Task<bool> SelectRangeAsync(TextRange range, string documentText, CancellationToken ct) =>
        await InvokeOrNull<bool?>(() =>
        {
            int driftHint = 0;
            var r = FindRange(_text!, documentText, range, ref driftHint);
            if (r is null) return false;
            try { FocusCore(); } catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException) { }
            r.Select();
            return true;
        }, ct).ConfigureAwait(false) ?? false;

    /// <summary>
    /// UIA thread only. True if the current selection is exactly <paramref name="expected"/>, allowing
    /// only for differences that can't change what typing over it does (see
    /// <see cref="CorrectionMath.SelectionMatches"/>). Rich editors (CKEditor, TinyMCE) may report one
    /// visual selection as several ranges split at formatting tags; those must be contiguous.
    /// </summary>
    private static bool SelectionReadsAs(TextPattern text, string expected)
    {
        var selection = text.GetSelection();
        if (selection is null || selection.Length == 0) return false;

        if (selection.Length == 1)
            return CorrectionMath.SelectionMatches(selection[0].GetText(-1), expected);

        for (int i = 1; i < selection.Length; i++)
        {
            if (selection[i - 1].CompareEndpoints(TextPatternRangeEndpoint.End, selection[i], TextPatternRangeEndpoint.Start) != 0)
                return false; // disjoint ranges: typing would replace text elsewhere too
        }
        return CorrectionMath.SelectionMatches(string.Concat(selection.Select(r => r.GetText(-1))), expected);
    }

    private static readonly TimeSpan ReselectAfter = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan SelectionSettleTimeout = TimeSpan.FromMilliseconds(1200);

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
