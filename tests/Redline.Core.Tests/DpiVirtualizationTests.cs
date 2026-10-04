using Redline.Core.Geometry;
using Redline.Core.Models;
using Xunit;

namespace Redline.Core.Tests;

public class DpiVirtualizationTests
{
    [Theory]
    [InlineData(96u, 120u, 1.25)]
    [InlineData(96u, 144u, 1.5)]
    [InlineData(120u, 120u, 1.0)]
    [InlineData(0u, 120u, 1.0)]
    [InlineData(96u, 0u, 1.0)]
    public void Scale_IsMonitorOverWindowDpi(uint window, uint monitor, double expected) =>
        Assert.Equal(expected, DpiVirtualization.Scale(window, monitor));

    [Fact]
    public void FromClientOrigin_ScalesTheOffsetAndSize()
    {
        // Control's client area starts at (500, 300) physical. The proxy reported a word 80 px right and
        // 20 px down in the control's own (96 dpi) units; at 125% that's 100 and 25 physical pixels.
        var fixedRect = DpiVirtualization.FromClientOrigin(new TextBounds(580, 320, 40, 16), 500, 300, 1.25);
        Assert.Equal(new TextBounds(600, 325, 50, 20), fixedRect);
    }

    [Fact]
    public void FromClientOrigin_IsIdentityWhenNotScaled()
    {
        var r = new TextBounds(580, 320, 40, 16);
        Assert.Equal(r, DpiVirtualization.FromClientOrigin(r, 500, 300, 1.0));
    }
}

public class Win32EditLinesTests
{
    [Theory]
    [InlineData("Edit", 0x0004, true)]
    [InlineData("EDIT", 0x50010004, true)]
    [InlineData("WindowsForms10.EDIT.app.0.230f04a_r8_ad1", 0x0004, true)]
    [InlineData("Edit", 0x0000, false)]                  // single-line edits already report the line height
    [InlineData("RichEditD2DPT", 0x0004, false)]         // Notepad: correct rectangles
    [InlineData("WindowsForms10.RichEdit20W.app.0.1", 0x0004, false)]
    public void OnlyMultilineClassicEdits(string className, int style, bool expected) =>
        Assert.Equal(expected, Win32EditLines.IsMultilineEdit(className, style));

    [Fact]
    public void WithLineHeight_GrowsDownward_NeverShrinks()
    {
        var em = new TextBounds(291, 386, 60, 37);
        Assert.Equal(new TextBounds(291, 386, 60, 50), Win32EditLines.WithLineHeight(em, 50));
        Assert.Equal(em, Win32EditLines.WithLineHeight(em, 30));
        Assert.Equal(TextBounds.Empty, Win32EditLines.WithLineHeight(TextBounds.Empty, 50));
    }
}
