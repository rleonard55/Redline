using Microsoft.Extensions.Logging;
using Redline.Analysis.Grmr;
using Redline.Core.Corrections;
using Redline.Core.Interfaces;
using Redline.Core.Models;
using Redline.Core.Pipeline;
using Redline.Windows;
using Redline.Windows.Corrections;

namespace Redline.App.Corrections;

/// <summary>
/// The user-facing correction flow: find the issue (at the caret, or picked in diagnostics), show the
/// suggestion popup beside it, then apply the choice — a replacement through the engine, or a
/// dictionary/ignore decision through the filters. A paragraph, sentence or selection can also be fixed as a
/// whole (<see cref="FixPopup"/>): every issue in it, plus the grammar model's whole-sentence versions, previewed
/// and then applied as one verified batch. Runs on the UI thread.
/// </summary>
public sealed class CorrectionController
{
    private static readonly TimeSpan AnalysisWait = TimeSpan.FromSeconds(1.5);

    /// <summary>How long the fix popup waits for the grammar model (about 0.5-1.5 s per sentence) before giving up on it.</summary>
    private static readonly TimeSpan ModelWait = TimeSpan.FromSeconds(10);

    private readonly SurfaceTracker _tracker;
    private readonly DocumentState _document;
    private readonly IssueCacheManager _cache;
    private readonly ReplacementEngine _engine;
    private readonly IPersonalDictionary _dictionary;
    private readonly IgnoreList _ignores;
    private readonly GrmrAnalyzer _grammarModel;
    private readonly ILogger<CorrectionController> _logger;
    private bool _busy;

    public CorrectionController(
        SurfaceTracker tracker, DocumentState document, IssueCacheManager cache, ReplacementEngine engine,
        IPersonalDictionary dictionary, IgnoreList ignores, GrmrAnalyzer grammarModel, ILogger<CorrectionController> logger)
    {
        _grammarModel = grammarModel;
        _tracker = tracker;
        _document = document;
        _cache = cache;
        _engine = engine;
        _dictionary = dictionary;
        _ignores = ignores;
        _logger = logger;
    }

    /// <summary>User-visible status (e.g. why a correction wasn't applied). Raised on the UI thread.</summary>
    public event Action<string, bool>? Notify;

    /// <summary>
    /// Hotkey: with text selected, fixes the selection as a whole; otherwise the issue under the caret, else the
    /// next one after it, else the first.
    /// </summary>
    public async Task ShowForCaretAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var adapter = _tracker.CurrentAdapter;
            if (adapter is null)
            {
                Notify?.Invoke("Put the cursor in a text field first.", false);
                return;
            }

            var issues = await WaitForCurrentIssuesAsync(adapter.Context.SurfaceId);
            if (issues is null)
            {
                Notify?.Invoke("Still checking this text — try again in a moment.", false);
                return;
            }

            var text = _document.Current?.Text;
            if (text is not null && _document.Current!.Version == issues.SnapshotVersion &&
                await adapter.GetSelectionAsync() is { Length: > 0 } selected && selected.End <= text.Length)
            {
                var range = CompositeFix.Trim(text, selected);
                if (range.Length > 0)
                {
                    var bounds = (await adapter.GetBoundsAsync([range], text))[0];
                    var fix = CompositeFix.Build(text, issues.SnapshotVersion, issues.Issues, range, FixScope.Selection);
                    if (fix.Edits.Count == 0 && !_grammarModel.IsAvailable)
                    {
                        Notify?.Invoke("No issues found in the selection.", false);
                        return;
                    }
                    await ShowFixAsync(adapter, [fix], 0, bounds.Count > 0 ? bounds[0] : null);
                    return;
                }
            }

            if (issues.Issues.Count == 0)
            {
                Notify?.Invoke("No issues found in this text field.", false);
                return;
            }

            var caret = await adapter.GetCaretOffsetAsync() ?? 0;
            var issue = issues.Issues.FirstOrDefault(i => i.StartOffset <= caret && caret <= i.StartOffset + i.Length)
                     ?? issues.Issues.FirstOrDefault(i => i.StartOffset > caret)
                     ?? issues.Issues[0];

            await ShowAndApplyAsync(adapter, issue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Correction flow failed");
            Notify?.Invoke("Something went wrong showing suggestions.", true);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Diagnostics window or the hover pill's "⋯": a specific issue on the current surface.</summary>
    public async Task ShowForIssueAsync(TextIssue issue)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var adapter = _tracker.CurrentAdapter;
            if (adapter is null || _document.Current?.Version != issue.SnapshotVersion)
            {
                Notify?.Invoke("That issue is out of date — the text or focus changed.", false);
                return;
            }
            await ShowAndApplyAsync(adapter, issue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Correction flow failed");
            Notify?.Invoke("Something went wrong showing suggestions.", true);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Gutter pill: the fix preview for <paramref name="paragraph"/> of snapshot <paramref name="version"/>, beside
    /// <paramref name="anchor"/> (the paragraph's visible extent, already measured by the overlay).
    /// </summary>
    public async Task ShowParagraphFixAsync(TextRange paragraph, long version, TextBounds anchor)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var adapter = _tracker.CurrentAdapter;
            var text = _document.Current?.Text;
            if (adapter is null || text is null || _document.Current!.Version != version ||
                _cache.Get(adapter.Context.SurfaceId, version) is not { } issues)
            {
                Notify?.Invoke("That paragraph is out of date — the text or focus changed.", false);
                return;
            }
            var first = issues.Issues.FirstOrDefault(i => i.StartOffset >= paragraph.Start && i.Range.End <= paragraph.End);
            await ShowFixAsync(adapter, Scopes(text, version, issues, first?.StartOffset ?? paragraph.Start), initialScope: 1, anchor);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Correction flow failed");
            Notify?.Invoke("Something went wrong showing the paragraph fix.", true);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Hover pill: apply <paramref name="issue"/>'s first suggestion straight away. The pill never takes
    /// focus, so the target is still in front; the engine verifies everything before it types.
    /// </summary>
    public async Task ApplyFirstSuggestionAsync(TextIssue issue)
    {
        if (_busy || issue.Suggestions.Count == 0) return;
        _busy = true;
        try
        {
            var adapter = _tracker.CurrentAdapter;
            if (adapter is null || _document.Current?.Version != issue.SnapshotVersion)
            {
                Notify?.Invoke("That issue is out of date — the text or focus changed.", false);
                return;
            }
            _logger.LogInformation("Pill apply for {Category} issue from {Analyzer}", issue.Category, issue.Analyzer);
            var result = await _engine.ApplyAsync(adapter, issue, issue.Suggestions[0]);
            if (!result.Succeeded)
                Notify?.Invoke(result.Message, result.Outcome == CorrectionOutcome.Unverified);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Correction flow failed");
            Notify?.Invoke("Something went wrong applying the suggestion.", true);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ShowAndApplyAsync(ITextSurfaceAdapter adapter, TextIssue issue)
    {
        // Verified geometry (the range must read as the flagged text), same as the squiggles.
        var text = _document.Current?.Text ?? string.Empty;
        var bounds = (await adapter.GetBoundsAsync([issue.Range], text))[0];
        var anchor = bounds.Count > 0 ? bounds[0] : (TextBounds?)null;

        // Offer the whole paragraph when there's more to fix in it than this issue, or the model can look at it.
        var scopes = Array.Empty<CompositeFix>();
        string? fixMoreLabel = null;
        if (text.Length > 0 && _cache.Get(adapter.Context.SurfaceId, issue.SnapshotVersion) is { } issues)
        {
            scopes = Scopes(text, issue.SnapshotVersion, issues, issue.StartOffset);
            var paragraph = scopes[1];
            if (paragraph.Edits.Count >= 2)
                fixMoreLabel = $"Fix this paragraph ({paragraph.Edits.Count} changes)...";
            else if (_grammarModel.IsAvailable)
                fixMoreLabel = "Fix this paragraph...";
        }

        var choice = await new SuggestionPopup(issue, fixMoreLabel).ShowNear(anchor);
        // Action + category only: rule ids and messages can quote the user's words.
        _logger.LogInformation("Popup choice {Choice} for {Category} issue from {Analyzer}", choice.Kind, issue.Category, issue.Analyzer);

        switch (choice.Kind)
        {
            case PopupChoiceKind.Suggestion:
                // The popup just closed and handed activation back to the target; Chromium drops
                // keystrokes that arrive mid-hand-back (the engine then falls back to paste).
                await Task.Delay(150);
                var result = await _engine.ApplyAsync(adapter, issue, choice.Replacement!);
                if (!result.Succeeded)
                    Notify?.Invoke(result.Message, result.Outcome == CorrectionOutcome.Unverified);
                break;

            case PopupChoiceKind.AddToDictionary:
                _dictionary.Add(issue.OriginalText);
                await adapter.FocusAsync();
                break;

            case PopupChoiceKind.Ignore:
                _ignores.IgnoreInSession(issue);
                await adapter.FocusAsync();
                break;

            case PopupChoiceKind.FixMore:
                await ShowFixAsync(adapter, scopes, initialScope: 1, anchor);
                break;

            case PopupChoiceKind.IgnoreRule:
                _ignores.IgnoreRule(issue.RuleId);
                await adapter.FocusAsync();
                break;

            case PopupChoiceKind.Cancel:
                await adapter.FocusAsync();
                break;
        }
    }

    /// <summary>
    /// Shows the fix preview for <paramref name="scopes"/> (all of one snapshot), asks the grammar model for its
    /// sentence versions meanwhile, and applies what the user picks as one batch.
    /// </summary>
    private async Task ShowFixAsync(ITextSurfaceAdapter adapter, IReadOnlyList<CompositeFix> scopes, int initialScope, TextBounds? anchor)
    {
        var text = scopes[0].Text;
        long version = scopes[0].SnapshotVersion;
        bool useModel = _grammarModel.IsAvailable;
        var popup = new FixPopup(scopes, initialScope, useModel);
        var choiceTask = popup.ShowNear(anchor);

        using var closed = new CancellationTokenSource();
        if (useModel)
        {
            // Sentences are inside the widest scope, so one request covers switching between them.
            var widest = scopes.MaxBy(s => s.Range.Length)!.Range;
            _ = LoadAlternativesAsync(popup, text, widest, closed.Token);
        }

        var edits = await choiceTask;
        closed.Cancel();
        _logger.LogInformation("Fix popup: {Count} edits chosen", edits?.Count ?? 0);
        if (edits is null || edits.Count == 0)
        {
            await adapter.FocusAsync();
            return;
        }

        await Task.Delay(150); // let activation go back to the target (see ShowAndApplyAsync)
        var result = await _engine.ApplyBatchAsync(adapter, version, edits);
        if (!result.Succeeded)
        {
            var message = result.Applied > 0 ? $"Applied {result.Applied} of {result.Total} changes. {result.Last.Message}" : result.Last.Message;
            Notify?.Invoke(message, result.Last.Outcome == CorrectionOutcome.Unverified);
        }
    }

    /// <summary>The sentence and paragraph holding <paramref name="offset"/>, in that order.</summary>
    private static CompositeFix[] Scopes(string text, long version, IssueSet issues, int offset) =>
    [
        CompositeFix.Build(text, version, issues.Issues, CompositeFix.SentenceAt(text, offset), FixScope.Sentence),
        CompositeFix.Build(text, version, issues.Issues, CompositeFix.ParagraphAt(text, offset), FixScope.Paragraph),
    ];

    private async Task LoadAlternativesAsync(FixPopup popup, string text, TextRange range, CancellationToken ct)
    {
        IReadOnlyList<SentenceAlternative> alternatives = [];
        try
        {
            var answers = await _grammarModel.GetSentenceAlternativesAsync(text, range, ModelWait, ct);
            alternatives = answers.Select(a => CompositeFix.ToAlternative(text, a.Sentence, a.Edits)).ToList();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Grammar model sentence versions failed");
        }
        if (!ct.IsCancellationRequested)
            popup.SetAlternatives(alternatives);
    }

    /// <summary>
    /// Issues for the current snapshot. Right after typing, analysis may still be pending (debounce
    /// + run), so wait briefly rather than offer offsets from an older version of the text.
    /// </summary>
    private async Task<IssueSet?> WaitForCurrentIssuesAsync(string surfaceId)
    {
        var deadline = DateTime.UtcNow + AnalysisWait;
        while (true)
        {
            var version = _document.Current?.Version;
            if (version is not null && _cache.Get(surfaceId, version) is { } issues)
                return issues;
            if (DateTime.UtcNow >= deadline)
                return null;
            await Task.Delay(50);
        }
    }
}
