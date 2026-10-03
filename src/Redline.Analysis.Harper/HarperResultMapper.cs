using System.Text.Json;
using System.Text.Json.Serialization;
using Redline.Core.Models;

namespace Redline.Analysis.Harper;

/// <summary>Converts harper-ffi JSON into <see cref="TextIssue"/>s.</summary>
public static class HarperResultMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <param name="includeSpelling">
    /// Harper has its own spellchecker; it's off by default because SpellAnalyzer owns spelling
    /// and running both produces duplicate squiggles.
    /// </param>
    public static IReadOnlyList<TextIssue> Map(string json, TextAnalysisRequest request, bool includeSpelling = false)
    {
        var response = JsonSerializer.Deserialize<LintResponse>(json, JsonOptions)
            ?? throw new FormatException("Empty Harper response.");

        if (!response.Ok)
            throw new InvalidOperationException($"Harper failed: {response.Error}");

        var issues = new List<TextIssue>(response.Lints.Count);
        foreach (var lint in response.Lints)
        {
            if (!includeSpelling && lint.Kind == "Spelling")
                continue;

            // Offsets are UTF-16 (converted on the Rust side); still never trust them blindly.
            if (lint.Start < 0 || lint.Length < 0 || lint.Start + lint.Length > request.Text.Length)
                continue;

            issues.Add(new TextIssue
            {
                StartOffset = request.ContextOffset + lint.Start,
                Length = lint.Length,
                OriginalText = request.Text.Substring(lint.Start, lint.Length),
                Category = MapCategory(lint.Kind),
                Message = lint.Message,
                Suggestions = lint.Suggestions,
                Analyzer = "Harper",
                SnapshotVersion = request.SnapshotVersion,
            });
        }

        return issues;
    }

    internal static IssueCategory MapCategory(string kind) => kind switch
    {
        "Spelling" or "Typo" => IssueCategory.Spelling,
        "Punctuation" or "Formatting" => IssueCategory.Punctuation,
        "Style" or "Readability" or "Enhancement" or "WordChoice" or "Redundancy" or "Regionalism" => IssueCategory.Style,
        _ => IssueCategory.Grammar,
    };

    private sealed record LintResponse
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public List<LintDto> Lints { get; init; } = new();
    }

    private sealed record LintDto
    {
        public int Start { get; init; }
        public int Length { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public int Priority { get; init; }
        public List<string> Suggestions { get; init; } = new();
    }
}
