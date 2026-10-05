using System.Runtime.InteropServices.ComTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Redline.Analysis.Interop;
using Redline.Core.Interfaces;
using Redline.Core.Models;

namespace Redline.Analysis;

/// <summary>
/// Spelling via the Windows Spell Checking API (the engine behind Windows' built-in
/// spellcheck). The OS tokenizes the text and also reports repeated words.
/// </summary>
/// <remarks>
/// The checker is created on a thread-pool (MTA) thread so that, whatever threading model the
/// factory registers, calls from other pool threads never marshal to the WPF UI thread.
/// The API's thread-safety isn't documented, so all calls are serialized.
/// </remarks>
public sealed class SpellAnalyzer : ITextAnalyzer
{
    private const int MaxSuggestions = 5;
    private const int S_OK = 0;

    private readonly IPersonalDictionary _personalDictionary;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly ISpellChecker? _checker;

    public SpellAnalyzer(IPersonalDictionary personalDictionary, string language = "en-US", ILogger<SpellAnalyzer>? logger = null)
    {
        _personalDictionary = personalDictionary;
        _logger = logger ?? NullLogger<SpellAnalyzer>.Instance;
        _checker = Task.Run(() => CreateChecker(language)).GetAwaiter().GetResult();
    }

    public string Name => "Spelling";
    public bool IsAvailable => _checker is not null;
    public string? UnavailableReason { get; private set; }
    public string? LanguageTag { get; private set; }

    /// <summary>Language tags with an installed Windows spelling dictionary; empty if the API is unavailable.</summary>
    public static IReadOnlyList<string> SupportedLanguages() => Task.Run(() =>
    {
        var languages = new List<string>();
        try
        {
            var factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
            IEnumString tags = factory.SupportedLanguages;
            var buffer = new string[1];
            while (tags.Next(1, buffer, IntPtr.Zero) == S_OK)
                languages.Add(buffer[0]);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
        }
        return (IReadOnlyList<string>)languages;
    }).GetAwaiter().GetResult();

    private ISpellChecker? CreateChecker(string language)
    {
        try
        {
            var factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
            foreach (var tag in new[] { language, "en-US" }.Distinct())
            {
                if (factory.IsSupported(tag))
                {
                    LanguageTag = tag;
                    if (tag != language)
                        _logger.LogWarning("Spelling language {Requested} not installed; using {Fallback}", language, tag);
                    return factory.CreateSpellChecker(tag);
                }
            }

            _logger.LogWarning("No supported spelling language found (requested {Language}); spelling disabled", language);
            UnavailableReason = $"Windows has no spelling dictionary for {language} (Settings > Time & language > Language & region).";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Windows Spell Checking API unavailable; spelling disabled");
            UnavailableReason = $"The Windows spell checker isn't available ({ex.GetType().Name}).";
        }

        return null;
    }

    public Task<IReadOnlyList<TextIssue>> AnalyzeAsync(TextAnalysisRequest request, CancellationToken ct)
    {
        if (_checker is null || request.Text.Length == 0)
            return Task.FromResult<IReadOnlyList<TextIssue>>(Array.Empty<TextIssue>());

        return Task.Run(() => Analyze(_checker, request, ct), ct);
    }

    private IReadOnlyList<TextIssue> Analyze(ISpellChecker checker, TextAnalysisRequest request, CancellationToken ct)
    {
        var issues = new List<TextIssue>();
        var text = request.Text;

        lock (_gate)
        {
            var errors = checker.Check(text);
            while (errors.Next(out var error) == S_OK && error is not null)
            {
                ct.ThrowIfCancellationRequested();

                int start = (int)error.StartIndex;
                int length = (int)error.Length;
                if (start < 0 || length <= 0 || start + length > text.Length)
                    continue;

                var word = text.Substring(start, length);
                var action = error.CorrectiveAction;

                if (action != CorrectiveAction.Delete && _personalDictionary.Contains(word))
                    continue;

                var (message, suggestions) = action switch
                {
                    CorrectiveAction.Delete => ($"Repeated word: '{word}'", new[] { string.Empty }),
                    CorrectiveAction.Replace => ($"Possible misspelling: '{word}'", new[] { error.Replacement }),
                    CorrectiveAction.GetSuggestions => ($"Possible misspelling: '{word}'", Suggest(checker, word)),
                    _ => ($"Possible misspelling: '{word}'", Array.Empty<string>()),
                };

                issues.Add(new TextIssue
                {
                    StartOffset = request.ContextOffset + start,
                    Length = length,
                    OriginalText = word,
                    Category = IssueCategory.Spelling,
                    Message = message,
                    Suggestions = suggestions,
                    Analyzer = Name,
                    RuleId = action == CorrectiveAction.Delete ? "Spelling:RepeatedWord" : "Spelling:Misspelling",
                    SnapshotVersion = request.SnapshotVersion,
                });
            }
        }

        return issues;
    }

    private static string[] Suggest(ISpellChecker checker, string word)
    {
        var results = new List<string>(MaxSuggestions);
        IEnumString suggestions = checker.Suggest(word);
        var buffer = new string[1];
        while (results.Count < MaxSuggestions && suggestions.Next(1, buffer, IntPtr.Zero) == S_OK)
            results.Add(buffer[0]);
        return results.ToArray();
    }
}
