using Redline.Core.Corrections;
using Redline.Core.Models;
using Xunit;

namespace Redline.Core.Tests;

public class DriftTests
{
    private static readonly string Obj = ((char)0xFFFC).ToString();

    [Fact]
    public void MaxPlausibleDrift_IsZeroForPlainText_AndGrowsWithObjectsAndBreaks()
    {
        Assert.Equal(0, CorrectionMath.MaxPlausibleDrift("plain text only", 10));
        Assert.Equal(3 + 1 + 2, CorrectionMath.MaxPlausibleDrift("a" + Obj + "\nb word", 6));
        Assert.Equal(64, CorrectionMath.MaxPlausibleDrift(string.Concat(Enumerable.Repeat(Obj, 100)) + "x", 100));
    }

    [Fact]
    public void DriftOrder_StartsAtHint_ThenSpiralsOut_WithinBounds()
    {
        Assert.Equal([2, 3, 1, 4, 0, -1, -2, -3, -4], CorrectionMath.DriftOrder(2, 4).ToList());
        Assert.Equal([0, 1, -1], CorrectionMath.DriftOrder(0, 1).ToList());
    }

    [Fact]
    public void ContextAround_StopsAtLineBreaksAndObjects()
    {
        var text = "me\n" + Obj + "Thsi is an tset.";
        int at = text.IndexOf("Thsi", StringComparison.Ordinal);

        var (before, after) = CorrectionMath.ContextAround(text, new TextRange(at, 4));

        Assert.Equal(string.Empty, before);   // an object sits right before the word
        Assert.Equal(" is an", after);        // six characters
    }

    [Theory]
    [InlineData("with me ", "with me\n", true)]     // Chromium's emptied last paragraph
    [InlineData("with me", "with me", true)]
    [InlineData("with me\n\n", "with me", true)]
    [InlineData("with me ", "with you\n", false)]   // content differs
    [InlineData(" with me", "with me", false)]       // leading whitespace is content
    [InlineData("a b", "a  b", false)]              // interior whitespace is content
    public void EquivalentForVerification_OnlyForgivesTrailingBlanks(string actual, string expected, bool equivalent)
    {
        Assert.Equal(equivalent, CorrectionMath.EquivalentForVerification(actual, expected));
    }

    [Theory]
    [InlineData("an", "an", true)]
    [InlineData("a\u200Bn", "an", true)]           // zero-width space at a formatting boundary
    [InlineData("\uFEFFan", "an", true)]           // zero-width no-break space
    [InlineData("line\r\nnext", "line\nnext", true)]
    [InlineData(" an", "an", false)]                // a real space must not be swallowed
    [InlineData("an ", "an", false)]
    [InlineData("\u00A0an", "an", false)]          // non-breaking space is visible
    [InlineData("an\n", "an", false)]              // so is a line break
    [InlineData("and", "an", false)]
    [InlineData(null, "an", false)]
    public void SelectionMatches_ToleratesOnlyInvisibleDifferences(string? actual, string expected, bool matches)
    {
        Assert.Equal(matches, CorrectionMath.SelectionMatches(Unescape(actual), Unescape(expected)!));
    }

    private static string? Unescape(string? s) => s is null ? null : System.Text.RegularExpressions.Regex.Unescape(s);

    [Fact]
    public void ContextAround_DistinguishesRepeatedWords()
    {
        var text = "the the cat";
        var first = CorrectionMath.ContextAround(text, new TextRange(0, 3));
        var second = CorrectionMath.ContextAround(text, new TextRange(4, 3));
        Assert.NotEqual(first, second);
    }
}
