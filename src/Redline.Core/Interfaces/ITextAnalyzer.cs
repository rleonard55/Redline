using Redline.Core.Models;

namespace Redline.Core.Interfaces;

public interface ITextAnalyzer
{
    /// <summary>Short name shown in diagnostics and stamped on <see cref="TextIssue.Analyzer"/>.</summary>
    string Name { get; }

    /// <summary>False when the analyzer's engine could not be loaded; the pipeline skips it.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// A supplementary analyzer only fills gaps: its issues are dropped where they overlap an issue
    /// from a primary analyzer, so a word never carries two competing underlines.
    /// </summary>
    bool IsSupplementary => false;

    Task<IReadOnlyList<TextIssue>> AnalyzeAsync(TextAnalysisRequest request, CancellationToken ct);
}
