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
