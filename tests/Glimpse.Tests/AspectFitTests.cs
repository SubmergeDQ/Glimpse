using Glimpse.Render;
using Xunit;

namespace Glimpse.Tests;

public sealed class AspectFitTests
{
    [Fact]
    public void WideSourceIntoSquareLeavesTopAndBottomBars()
    {
        var viewport = AspectFit.Compute(1920, 1080, 800, 800);

        Assert.Equal(0, viewport.X, 3);
        Assert.Equal(175, viewport.Y, 3);
        Assert.Equal(800, viewport.Width, 3);
        Assert.Equal(450, viewport.Height, 3);
    }

    [Fact]
    public void TallSourceIntoWideTargetLeavesLeftAndRightBars()
    {
        var viewport = AspectFit.Compute(1080, 1920, 800, 600);

        Assert.Equal(338, viewport.Width, 3);
        Assert.Equal(600, viewport.Height, 3);
        Assert.Equal(231, viewport.X, 3);
        Assert.Equal(0, viewport.Y, 3);
    }

    [Fact]
    public void InvalidSourceSizeFallsBackToFullTarget()
    {
        var viewport = AspectFit.Compute(0, 0, 640, 360);

        Assert.Equal(0, viewport.X, 3);
        Assert.Equal(0, viewport.Y, 3);
        Assert.Equal(640, viewport.Width, 3);
        Assert.Equal(360, viewport.Height, 3);
    }

    [Fact]
    public void InvalidTargetSizeReturnsMinimalViewport()
    {
        var viewport = AspectFit.Compute(1920, 1080, 0, 0);

        Assert.Equal(1, viewport.Width, 3);
        Assert.Equal(1, viewport.Height, 3);
    }
}
