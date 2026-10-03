using Redline.Core.Models;

namespace Redline.Core.Text;

public static class TextDiff
{
    /// <summary>
    /// Computes the single contiguous change that transforms <paramref name="oldText"/>
    /// into <paramref name="newText"/> by trimming the common prefix and suffix.
    /// Multiple discontiguous edits collapse into one covering change, which is fine
    /// for invalidation purposes.
    /// </summary>
    public static TextChange Compute(string oldText, string newText)
    {
        int prefix = 0;
        int maxPrefix = Math.Min(oldText.Length, newText.Length);
        while (prefix < maxPrefix && oldText[prefix] == newText[prefix])
            prefix++;

        // Don't let the change start in the middle of a surrogate pair.
        if (prefix > 0 && prefix < maxPrefix && char.IsHighSurrogate(newText[prefix - 1]))
            prefix--;

        int suffix = 0;
        int maxSuffix = maxPrefix - prefix;
        while (suffix < maxSuffix &&
               oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix])
            suffix++;

        // ...or end in the middle of one.
        if (suffix > 0 && char.IsLowSurrogate(newText[newText.Length - suffix]))
            suffix--;

        return new TextChange(prefix, oldText.Length - prefix - suffix, newText.Length - prefix - suffix);
    }
}
