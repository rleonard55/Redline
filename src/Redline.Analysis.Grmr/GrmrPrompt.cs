namespace Redline.Analysis.Grmr;

/// <summary>
/// GRMR-V3-G1B's chat format (from its tokenizer_config.json): the input goes in a "text" turn and
/// the model answers in a "corrected" turn. The BOS token is added by the tokenizer.
/// </summary>
public static class GrmrPrompt
{
    public const string EndOfTurn = "<end_of_turn>";

    public static string Build(string text) =>
        "<start_of_turn>text" + (char)0x0A + text + EndOfTurn + (char)0x0A + "<start_of_turn>corrected" + (char)0x0A;

    /// <summary>Corrections are about as long as the input (~4 chars per token); this leaves ample room.</summary>
    public static int MaxOutputTokens(string text) => text.Length / 2 + 32;

    /// <summary>
    /// The corrected text from the raw generation, or null when the model ran out of tokens without
    /// finishing (a runaway generation is not a correction).
    /// </summary>
    public static string? ParseOutput(string raw, bool hitTokenLimit)
    {
        int end = raw.IndexOf(EndOfTurn, StringComparison.Ordinal);
        if (end < 0 && hitTokenLimit)
            return null;
        var text = (end >= 0 ? raw[..end] : raw).Trim();
        return text.Length == 0 ? null : text;
    }
}
