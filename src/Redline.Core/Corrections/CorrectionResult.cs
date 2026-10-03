namespace Redline.Core.Corrections;

public enum CorrectionOutcome
{
    /// <summary>The document now reads exactly as expected.</summary>
    Applied,

    /// <summary>A pre-check failed; the document was not touched.</summary>
    Rejected,

    /// <summary>An edit was attempted, didn't verify, and was undone (document back to its original text).</summary>
    Reverted,

    /// <summary>The document changed, but not as expected, and Undo didn't restore it. The user should look.</summary>
    Unverified,
}

/// <summary>Outcome of applying one correction. Carries no document text, so it is safe to log.</summary>
public sealed record CorrectionResult(CorrectionOutcome Outcome, string Method, string Message, TimeSpan Duration)
{
    public bool Succeeded => Outcome == CorrectionOutcome.Applied;

    public override string ToString() => $"{Outcome} via {Method} in {Duration.TotalMilliseconds:F0} ms: {Message}";
}
