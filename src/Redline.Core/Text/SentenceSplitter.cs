using Redline.Core.Models;

namespace Redline.Core.Text;

/// <summary>
/// Splits text into sentence-sized pieces for analyzers that work one sentence at a time (the
/// grammar model). Deliberately simple: a break after . ! ? (plus closing quotes or brackets)
/// followed by whitespace, and at every line break. Abbreviations ("e.g. this") split early,
/// which only costs a little context.
/// </summary>
public static class SentenceSplitter
{
    /// <summary>Trimmed sentence ranges that contain at least one letter, in document order.</summary>
    public static IReadOnlyList<TextRange> Split(string text)
    {
        var result = new List<TextRange>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (IsLineBreak(c))
            {
                Add(text, start, i, result);
                start = i + 1;
            }
            else if (c is '.' or '!' or '?')
            {
                int end = i + 1;
                while (end < text.Length && (text[end] is '.' or '!' or '?' || IsCloser(text[end])))
                    end++;
                if (end == text.Length || char.IsWhiteSpace(text[end]))
                {
                    Add(text, start, end, result);
                    start = end;
                }
                i = end - 1;
            }
        }
        Add(text, start, text.Length, result);
        return result;
    }

    private static void Add(string text, int start, int end, List<TextRange> result)
    {
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        for (int i = start; i < end; i++)
        {
            if (char.IsLetter(text[i]))
            {
                result.Add(new TextRange(start, end - start));
                return;
            }
        }
    }

    // Closing quotes and brackets that end a sentence along with its punctuation.
    private static bool IsCloser(char c) => c is '"' or '\'' or ')' or ']' or (char)0x2019 or (char)0x201D;

    // LF, CR, paragraph separator, vertical tab (Word's manual line break), line separator
    private static bool IsLineBreak(char c) => c is (char)0x0A or (char)0x0D or (char)0x2029 or (char)0x0B or (char)0x2028;
}
