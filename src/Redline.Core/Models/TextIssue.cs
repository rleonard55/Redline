namespace Redline.Core.Models;

public enum IssueCategory
{
    Spelling,
    Grammar,
    Style,
    Punctuation,
    Other
}

/// <summary>
/// A diagnostic issue discovered in the text snapshot by an analyzer.
/// </summary>
public record TextIssue
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required int StartOffset { get; init; }
    public required int Length { get; init; }
    public required string OriginalText { get; init; }
    public required IssueCategory Category { get; init; }
    public required string Message { get; init; }
    public IReadOnlyList<string> Suggestions { get; init; } = Array.Empty<string>();
    public string Analyzer { get; init; } = string.Empty;
    public double Confidence { get; init; } = 1.0;
    public long SnapshotVersion { get; init; }

    /// <summary>
    /// Identifies the check that produced the issue, for "Ignore rule". Analyzers set it when they
    /// can name the rule more precisely than analyzer + category.
    /// </summary>
    public string RuleId { get => _ruleId ?? $"{Analyzer}:{Category}"; init => _ruleId = value; }
    private readonly string? _ruleId;

    public TextRange Range => new(StartOffset, Length);
}
