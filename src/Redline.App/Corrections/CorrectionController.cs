using Microsoft.Extensions.Logging;
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
/// dictionary/ignore decision through the filters. Runs on the UI thread.
/// </summary>
public sealed class CorrectionController
{
    private static readonly TimeSpan AnalysisWait = TimeSpan.FromSeconds(1.5);

    private readonly SurfaceTracker _tracker;
    private readonly DocumentState _document;
    private readonly IssueCacheManager _cache;
    private readonly ReplacementEngine _engine;
    private readonly IPersonalDictionary _dictionary;
    private readonly IgnoreList _ignores;
    private readonly ILogger<CorrectionController> _logger;
    private bool _busy;

    public CorrectionController(
        SurfaceTracker tracker, DocumentState document, IssueCacheManager cache, ReplacementEngine engine,
        IPersonalDictionary dictionary, IgnoreList ignores, ILogger<CorrectionController> logger)
    {
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

    /// <summary>Hotkey: the issue under the caret, else the next one after it, else the first.</summary>
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

    /// <summary>Diagnostics window: a specific issue on the current surface.</summary>
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

    private async Task ShowAndApplyAsync(ITextSurfaceAdapter adapter, TextIssue issue)
    {
        // Verified geometry (the range must read as the flagged text), same as the squiggles.
        var text = _document.Current?.Text ?? string.Empty;
        var bounds = (await adapter.GetBoundsAsync([issue.Range], text))[0];
        var choice = await new SuggestionPopup(issue).ShowNear(bounds.Count > 0 ? bounds[0] : null);
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
