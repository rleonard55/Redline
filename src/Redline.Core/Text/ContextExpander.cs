using Redline.Core.Models;

namespace Redline.Core.Text;

public static class ContextExpander
{
    /// <summary>
    /// Expands <paramref name="range"/> outward to paragraph (line) boundaries in
    /// <paramref name="text"/>. The result starts just after a line break (or at 0) and ends
    /// just before a line break (or at end of text).
    /// </summary>
    public static TextRange ToParagraph(string text, TextRange range)
    {
        int start = Math.Clamp(range.Start, 0, text.Length);
        int end = Math.Clamp(range.End, start, text.Length);

        while (start > 0 && !IsLineBreak(text[start - 1]))
            start--;
        while (end < text.Length && !IsLineBreak(text[end]))
            end++;

        return new TextRange(start, end - start);
    }

    // LF, CR, paragraph separator, vertical tab (Word's manual line break)
    private static bool IsLineBreak(char c) => c is (char)0x0A or (char)0x0D or (char)0x2029 or (char)0x0B;
}
