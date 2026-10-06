using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Redline.Core.Corrections;
using Redline.Core.Diagnostics;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Pipeline;
using Redline.Windows.Automation;
using Redline.Windows.Input;

namespace Redline.Windows.Corrections;

public enum ReplacementStrategy
{
    /// <summary>
    /// Select the exact range via UIA, then replace it with <c>EM_REPLACESEL</c> (Win32 Edit/RichEdit controls only).
    /// No synthesized keystrokes; keeps undo history.
    /// </summary>
    EditMessage,

    /// <summary>Select the exact range via UIA, then type the replacement. Keeps undo history and formatting.</summary>
    SelectAndType,

    /// <summary>Select the range, then paste the replacement through a guarded, restored clipboard.</summary>
    SelectAndPaste,

    /// <summary>Replace the control's whole value. Only for controls without TextPattern.</summary>
    SetValue,
}

public sealed record ReplacementOptions
{
    public TimeSpan FocusTimeout { get; init; } = TimeSpan.FromSeconds(1.5);
    public TimeSpan ModifierReleaseTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan VerifyTimeout { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Restricts the engine to these strategies, in this order (diagnostics and tests).</summary>
    public IReadOnlyList<ReplacementStrategy>? Strategies { get; init; }
}

/// <summary>
/// Applies one suggestion to the live document with every check we can make: the issue must belong
/// to the current snapshot, the document must still read exactly as analyzed, the target must hold
/// keyboard focus, the selected range must read exactly as the flagged text, and afterwards the
/// document must read exactly as expected — otherwise the edit is undone (risk R5, R9).
/// </summary>
/// <remarks>
/// Strategy order deliberately differs from the plan's (ValuePattern first): SetValue rewrites the
/// whole document, which discards undo history and formatting and can clobber concurrent typing,
/// and Phase 0 showed it silently fails in CKEditor. Range selection + typing is the most faithful
/// edit, so it goes first; SetValue is reserved for controls that expose no text ranges. In Win32 Edit/RichEdit
/// controls an edit message replaces the verified selection before any keystrokes are tried: on a locked-down PC the
/// process died while typing corrections (likely security software stopping injected keystrokes), and
/// <see cref="CorrectionGuard"/> moves a strategy that died mid-edit to the end.
/// The remaining risk is the few milliseconds between the last check and the keystrokes landing.
/// </remarks>
public sealed class ReplacementEngine
{
    private readonly UiaDispatcher _uia;
    private readonly DocumentState _document;
    private readonly ReplacementOptions _options;
    private readonly ILogger _logger;
    private readonly PerfCounters? _perf;
    private readonly CompatibilityLog? _compatibility;
    private readonly CorrectionGuard? _guard;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public ReplacementEngine(UiaDispatcher uia, DocumentState document, ReplacementOptions? options = null, ILogger<ReplacementEngine>? logger = null,
        PerfCounters? perf = null, CompatibilityLog? compatibility = null, CorrectionGuard? guard = null)
    {
        _perf = perf;
        _compatibility = compatibility;
        _guard = guard;
        _uia = uia;
        _document = document;
        _options = options ?? new ReplacementOptions();
        _logger = logger ?? NullLogger<ReplacementEngine>.Instance;
    }

    public async Task<CorrectionResult> ApplyAsync(ITextSurfaceAdapter adapter, TextIssue issue, string replacement, CancellationToken ct = default)
    {
        await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await ApplyCoreAsync(adapter, issue, replacement, sw, ct).ConfigureAwait(false);
            _perf?.Record("correction", result.Duration);
            _compatibility?.Correction(adapter.Context, result);
            _logger.LogInformation("Correction in {Process} ({Category}, {Rule}): {Result}",
                adapter.Context.ProcessName, issue.Category, issue.Analyzer, result);
            return result;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// Applies several non-overlapping edits of the snapshot <paramref name="snapshotVersion"/> as one fix, back to
    /// front so earlier offsets never shift. The snapshot is checked once; before each edit the live text must read
    /// as the previous edit left it, and every edit is selected, typed and verified like a single correction. A
    /// failed edit is undone and the rest are skipped; the edits already applied stay (each was verified).
    /// </summary>
    public async Task<BatchCorrectionResult> ApplyBatchAsync(ITextSurfaceAdapter adapter, long snapshotVersion, IReadOnlyList<FixEdit> edits, CancellationToken ct = default)
    {
        await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            var (applied, result) = await ApplyBatchCoreAsync(adapter, snapshotVersion, edits, sw, ct).ConfigureAwait(false);
            _perf?.Record("correction", result.Duration);
            _compatibility?.Correction(adapter.Context, result);
            _logger.LogInformation("Combined correction in {Process} ({Applied} of {Total} edits): {Result}",
                adapter.Context.ProcessName, applied, edits.Count, result);
            return new BatchCorrectionResult(applied, edits.Count, result);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<(int Applied, CorrectionResult Result)> ApplyBatchCoreAsync(
        ITextSurfaceAdapter adapter, long snapshotVersion, IReadOnlyList<FixEdit> edits, Stopwatch sw, CancellationToken ct)
    {
        CorrectionResult Reject(string message) => new(CorrectionOutcome.Rejected, "none", message, sw.Elapsed);

        var snapshot = _document.Current;
        if (_document.SurfaceId != adapter.Context.SurfaceId || snapshot is null)
            return (0, Reject("That text field is no longer active."));
        if (snapshotVersion != snapshot.Version)
            return (0, Reject("The text changed after these issues were found."));
        if (!adapter.Capabilities.CanReplaceText)
            return (0, Reject("This field doesn't allow Redline to edit it."));
        if (edits.Count == 0)
            return (0, Reject("Nothing to change."));

        var ordered = edits.OrderByDescending(e => e.Range.Start).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var edit = ordered[i];
            if (edit.Range.Start < 0 || edit.Range.End > snapshot.Length || snapshot.GetSubstring(edit.Range) != edit.Original)
                return (0, Reject("The issues no longer match the text."));
            if (i + 1 < ordered.Count && ordered[i + 1].Range.End > edit.Range.Start)
                return (0, Reject("The changes overlap."));
        }

        var current = snapshot.Text;
        int applied = 0;
        string method = "none";
        foreach (var edit in ordered)
        {
            // The live text must read as the last verified edit left it (verification forgives trailing blanks at
            // the end of the document; those can't move the earlier ranges, so continue from the live text).
            var live = await adapter.ReadTextAsync(ct).ConfigureAwait(false);
            if (live is null || !(live == current || (applied > 0 && CorrectionMath.EquivalentForVerification(live, current))) ||
                edit.Range.End > live.Length || live.Substring(edit.Range.Start, edit.Range.Length) != edit.Original)
                return (applied, Reject(applied == 0 ? "The text changed after these issues were found." : "The text changed while fixing it, so Redline stopped."));
            current = live;

            var expected = CorrectionMath.Apply(current, edit.Range, edit.Replacement);
            var result = await ApplyEditAsync(adapter, current, edit.Range, edit.Replacement, expected, sw, ct).ConfigureAwait(false);
            if (!result.Succeeded)
                return (applied, result);
            applied++;
            method = result.Method;
            current = expected;
        }

        return (applied, new CorrectionResult(CorrectionOutcome.Applied, method, "Applied.", sw.Elapsed));
    }

    private async Task<CorrectionResult> ApplyCoreAsync(ITextSurfaceAdapter adapter, TextIssue issue, string replacement, Stopwatch sw, CancellationToken ct)
    {
        CorrectionResult Result(CorrectionOutcome outcome, string method, string message) => new(outcome, method, message, sw.Elapsed);
        CorrectionResult Reject(string message) => Result(CorrectionOutcome.Rejected, "none", message);

        // 1. Stale-state protection: the issue must describe the snapshot we're still tracking.
        var snapshot = _document.Current;
        if (_document.SurfaceId != adapter.Context.SurfaceId || snapshot is null)
            return Reject("That text field is no longer active.");
        if (issue.SnapshotVersion != snapshot.Version)
            return Reject("The text changed after this issue was found.");
        if (!adapter.Capabilities.CanReplaceText)
            return Reject("This field doesn't allow Redline to edit it.");
        if (issue.StartOffset < 0 || issue.Range.End > snapshot.Length || snapshot.GetSubstring(issue.Range) != issue.OriginalText)
            return Reject("The issue no longer matches the text.");

        var original = snapshot.Text;
        var range = replacement.Length == 0 ? CorrectionMath.ExpandDeletion(original, issue.Range) : issue.Range;
        var expected = CorrectionMath.Apply(original, range, replacement);

        // 2. Source verification: the live document must still read exactly as analyzed.
        if (await adapter.ReadTextAsync(ct).ConfigureAwait(false) != original)
            return Reject("The text changed after this issue was found.");

        return await ApplyEditAsync(adapter, original, range, replacement, expected, sw, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Steps 3-4 of a correction: focus the target, then replace <paramref name="range"/> of <paramref name="original"/>
    /// (which the live document reads as) and verify it reads as <paramref name="expected"/>, undoing it if not.
    /// </summary>
    private async Task<CorrectionResult> ApplyEditAsync(ITextSurfaceAdapter adapter, string original, TextRange range, string replacement,
        string expected, Stopwatch sw, CancellationToken ct)
    {
        CorrectionResult Result(CorrectionOutcome outcome, string method, string message) => new(outcome, method, message, sw.Elapsed);
        CorrectionResult Reject(string message) => Result(CorrectionOutcome.Rejected, "none", message);

        var process = adapter.Context.ProcessName;
        void Step(ReplacementStrategy? strategy, string step)
        {
            var name = strategy?.ToString() ?? CorrectionGuard.NoStrategy;
            _guard?.Mark(name, step, process);
            _logger.LogDebug("Correction step {Step} ({Strategy}) in {Process}", step, name, process);
        }

        string? appliedWith = null;
        try
        {
            // 3. Focus the target (the popup or diagnostics window may have it).
            Step(null, "focus");
            if (!await EnsureFocusAsync(adapter, ct).ConfigureAwait(false))
                return Reject("Couldn't move keyboard focus back to the text field.");

            // 4. Strategies, most faithful first. Each either changes nothing (try the next), or produces a
            //    final outcome. One that crashed the process last time (CorrectionGuard) goes last.
            var strategies = _options.Strategies ?? DefaultStrategies(adapter);
            if (_guard is not null && _options.Strategies is null)
                strategies = _guard.Order(strategies, s => s.ToString());

            string lastReason = "No editing method is available for this field.";
            bool attempted = false;
            foreach (var strategy in strategies)
            {
                if (strategy == ReplacementStrategy.SelectAndType && replacement.IndexOfAny(['\r', '\n']) >= 0)
                    continue; // typed line breaks become Enter presses, which apps interpret differently

                Step(strategy, "select");
                var (prepared, reason, cleanup) = await PrepareAsync(strategy, adapter, range, original, replacement, ct).ConfigureAwait(false);
                if (!prepared)
                {
                    // Keep the first failure: later strategies usually fail for the same root cause
                    // ("can't select") or for a reason that doesn't apply ("paste can't delete").
                    if (!attempted) lastReason = reason;
                    attempted = true;
                    continue;
                }

                try
                {
                    // Held modifiers would turn typed or pasted keys into shortcuts; messages aren't keys.
                    if (UsesKeystrokes(strategy) &&
                        !await KeyboardInput.WaitForModifiersReleasedAsync(_options.ModifierReleaseTimeout, ct).ConfigureAwait(false))
                        return Reject("Release Ctrl/Alt/Shift/Win and try again.");
                    if (strategy != ReplacementStrategy.SetValue && !await adapter.HasKeyboardFocusAsync(ct).ConfigureAwait(false))
                        return Reject("Keyboard focus moved away from the text field.");

                    Step(strategy, "input");
                    if (!await ExecuteAsync(strategy, adapter, replacement, ct).ConfigureAwait(false))
                    {
                        lastReason = strategy == ReplacementStrategy.EditMessage
                            ? "The text field didn't accept the edit."
                            : "Windows rejected the synthesized input.";
                        continue;
                    }

                    Step(strategy, "verify");
                    var outcome = await VerifyAsync(adapter, original, expected, ct).ConfigureAwait(false);
                    if (outcome == Verification.Matched)
                    {
                        appliedWith = strategy.ToString();
                        return Result(CorrectionOutcome.Applied, strategy.ToString(), "Applied.");
                    }
                    if (outcome == Verification.Unchanged)
                    {
                        lastReason = $"{strategy} had no effect.";
                        continue;
                    }

                    Step(strategy, "undo");
                    return await UndoAsync(adapter, original, strategy, sw, ct).ConfigureAwait(false);
                }
                finally
                {
                    if (cleanup is not null)
                        await cleanup().ConfigureAwait(false);
                }
            }

            return Reject(lastReason);
        }
        finally
        {
            _guard?.Finish(appliedWith);
        }
    }

    /// <summary>
    /// Edit messages first where the control is a Win32 Edit/RichEdit (no keystrokes at all), then typing, then paste;
    /// SetValue only for controls without text ranges.
    /// </summary>
    private static IReadOnlyList<ReplacementStrategy> DefaultStrategies(ITextSurfaceAdapter adapter)
    {
        if (!adapter.Capabilities.CanGetSelection)
            return [ReplacementStrategy.SetValue];
        return EditControlMessages.IsEditWindow(new IntPtr(adapter.Context.NativeWindowHandle))
            ? [ReplacementStrategy.EditMessage, ReplacementStrategy.SelectAndType, ReplacementStrategy.SelectAndPaste]
            : [ReplacementStrategy.SelectAndType, ReplacementStrategy.SelectAndPaste];
    }

    private static bool UsesKeystrokes(ReplacementStrategy strategy) =>
        strategy is ReplacementStrategy.SelectAndType or ReplacementStrategy.SelectAndPaste;

    /// <summary>Sets up a strategy. Never changes document text; may change the selection or clipboard.</summary>
    private async Task<(bool Ok, string Reason, Func<Task>? Cleanup)> PrepareAsync(
        ReplacementStrategy strategy, ITextSurfaceAdapter adapter, TextRange range, string original, string replacement, CancellationToken ct)
    {
        switch (strategy)
        {
            case ReplacementStrategy.EditMessage:
                if (!EditControlMessages.IsEditWindow(new IntPtr(adapter.Context.NativeWindowHandle)))
                    return (false, "This field isn't a standard Windows text box.", null);
                return await adapter.SelectAsync(range, original, ct).ConfigureAwait(false)
                    ? (true, string.Empty, null)
                    : (false, "Couldn't select exactly the flagged text.", null);

            case ReplacementStrategy.SelectAndType:
                return await adapter.SelectAsync(range, original, ct).ConfigureAwait(false)
                    ? (true, string.Empty, null)
                    : (false, "Couldn't select exactly the flagged text.", null);

            case ReplacementStrategy.SelectAndPaste:
                if (replacement.Length == 0)
                    return (false, "Paste can't express a deletion.", null);
                if (!await adapter.SelectAsync(range, original, ct).ConfigureAwait(false))
                    return (false, "Couldn't select exactly the flagged text.", null);
                var scope = await ClipboardScope.TryReplaceAsync(_uia, replacement).ConfigureAwait(false);
                if (scope is null)
                    return (false, "The clipboard holds content Redline can't restore, so it wasn't used.", null);
                return (true, string.Empty, async () =>
                {
                    // Give the target time to read the clipboard before restoring it.
                    await Task.Delay(150).ConfigureAwait(false);
                    if (!await scope.RestoreAsync().ConfigureAwait(false))
                        _logger.LogInformation("Clipboard changed by someone else during paste; not restoring");
                });

            case ReplacementStrategy.SetValue:
                // SetValue applies immediately; it re-checks the current value inside the same UIA call.
                var expected = CorrectionMath.Apply(original, range, replacement);
                return await adapter.SetValueAsync(original, expected, ct).ConfigureAwait(false)
                    ? (true, string.Empty, null)
                    : (false, "The field's value couldn't be set (or changed first).", null);

            default:
                return (false, $"Unknown strategy {strategy}.", null);
        }
    }

    private static async Task<bool> ExecuteAsync(ReplacementStrategy strategy, ITextSurfaceAdapter adapter, string replacement, CancellationToken ct) => strategy switch
    {
        // The verified selection must still be in the control that has focus; the message replaces whatever is selected.
        ReplacementStrategy.EditMessage => await Task.Run(() =>
        {
            var hwnd = new IntPtr(adapter.Context.NativeWindowHandle);
            return EditControlMessages.IsFocusedEditWindow(hwnd) && EditControlMessages.ReplaceSelection(hwnd, replacement);
        }, ct).ConfigureAwait(false),
        ReplacementStrategy.SelectAndType => replacement.Length == 0
            ? await KeyboardInput.PressAsync(KeyboardInput.VK_DELETE, ct: ct).ConfigureAwait(false)
            : await KeyboardInput.TypeTextAsync(replacement, ct: ct).ConfigureAwait(false),
        ReplacementStrategy.SelectAndPaste => await KeyboardInput.PressAsync(KeyboardInput.VK_V, KeyboardInput.VK_CONTROL, ct).ConfigureAwait(false),
        ReplacementStrategy.SetValue => true, // already applied in PrepareAsync
        _ => false,
    };

    private async Task<bool> EnsureFocusAsync(ITextSurfaceAdapter adapter, CancellationToken ct)
    {
        if (await adapter.HasKeyboardFocusAsync(ct).ConfigureAwait(false))
            return true;

        await adapter.FocusAsync(ct).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + _options.FocusTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, ct).ConfigureAwait(false);
            if (await adapter.HasKeyboardFocusAsync(ct).ConfigureAwait(false))
            {
                // A window that was just activated can drop the first keystrokes (seen in Chromium
                // PWAs); let activation finish before anything is typed.
                await Task.Delay(ActivationSettle, ct).ConfigureAwait(false);
                return true;
            }
        }
        return false;
    }

    private static readonly TimeSpan ActivationSettle = TimeSpan.FromMilliseconds(150);

    private enum Verification { Matched, Unchanged, Other }

    /// <summary>Polls until the document reads as expected, or settles on something else.</summary>
    private async Task<Verification> VerifyAsync(ITextSurfaceAdapter adapter, string original, string expected, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _options.VerifyTimeout;
        string? previous = null;
        int stableReads = 0;

        while (true)
        {
            await Task.Delay(40, ct).ConfigureAwait(false);
            var text = await adapter.ReadTextAsync(ct).ConfigureAwait(false);
            if (text is not null && CorrectionMath.EquivalentForVerification(text, expected))
                return Verification.Matched;

            // Something other than our edit, and it's stopped changing: no point waiting longer.
            stableReads = text == previous ? stableReads + 1 : 0;
            previous = text;
            if (text is not null && text != original && stableReads >= 3)
                return Verification.Other;

            if (DateTime.UtcNow >= deadline)
                return text == original ? Verification.Unchanged : Verification.Other;
        }
    }

    private async Task<CorrectionResult> UndoAsync(ITextSurfaceAdapter adapter, string original, ReplacementStrategy strategy, Stopwatch sw, CancellationToken ct)
    {
        // Only undo in the field we just edited: EM_UNDO to the control we sent the edit to, else Ctrl+Z.
        bool sent = strategy == ReplacementStrategy.EditMessage
            ? await Task.Run(() => EditControlMessages.Undo(new IntPtr(adapter.Context.NativeWindowHandle)), ct).ConfigureAwait(false)
            : await adapter.HasKeyboardFocusAsync(ct).ConfigureAwait(false) &&
              await KeyboardInput.WaitForModifiersReleasedAsync(_options.ModifierReleaseTimeout, ct).ConfigureAwait(false) &&
              await KeyboardInput.PressAsync(KeyboardInput.VK_Z, KeyboardInput.VK_CONTROL, ct).ConfigureAwait(false);
        if (sent)
        {
            var deadline = DateTime.UtcNow + _options.VerifyTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
                if (await adapter.ReadTextAsync(ct).ConfigureAwait(false) == original)
                    return new(CorrectionOutcome.Reverted, strategy.ToString(), "The edit didn't come out as expected, so it was undone.", sw.Elapsed);
            }
        }

        return new(CorrectionOutcome.Unverified, strategy.ToString(),
            "The text changed, but not as expected, and Undo didn't restore it. Please check the document.", sw.Elapsed);
    }
}
