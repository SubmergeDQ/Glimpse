using System.Drawing;
using Glimpse.Ui;
using Xunit;

namespace Glimpse.Tests;

public sealed class AspectSizingTests
{
    private const double PortraitAspect = 1080.0 / 1920.0;

    [Fact]
    public void RightEdgeDerivesHeightFromWidth()
    {
        var bounds = new Rectangle(100, 100, 500, 400);

        Rectangle result = AspectSizing.Constrain(bounds, SizingEdge.Right, PortraitAspect, 160, 90);

        Assert.Equal(new Rectangle(100, 100, 500, 889), result);
    }

    [Fact]
    public void BottomEdgeDerivesWidthFromHeight()
    {
        var bounds = new Rectangle(100, 100, 400, 600);

        Rectangle result = AspectSizing.Constrain(bounds, SizingEdge.Bottom, PortraitAspect, 160, 90);

        Assert.Equal(new Rectangle(100, 100, 338, 600), result);
    }

    [Fact]
    public void TopLeftCornerKeepsBottomRightAnchor()
    {
        var bounds = new Rectangle(100, 100, 400, 300);

        Rectangle result = AspectSizing.Constrain(bounds, SizingEdge.TopLeft, PortraitAspect, 160, 90);

        Assert.Equal(bounds.Right, result.Right);
        Assert.Equal(bounds.Bottom, result.Bottom);
        Assert.Equal(400, result.Width);
        Assert.Equal(711, result.Height);
    }

    [Fact]
    public void CornerUsesDominantDimension()
    {
        var bounds = new Rectangle(0, 0, 200, 500);

        Rectangle result = AspectSizing.Constrain(bounds, SizingEdge.BottomRight, PortraitAspect, 160, 90);

        Assert.Equal(281, result.Width);
        Assert.Equal(500, result.Height);
    }

    [Fact]
    public void LandscapeSourceUsesLandscapeAspect()
    {
        var bounds = new Rectangle(0, 0, 600, 100);

        Rectangle result = AspectSizing.Constrain(bounds, SizingEdge.Right, 1920.0 / 1080.0, 160, 90);

        Assert.Equal(600, result.Width);
        Assert.Equal(338, result.Height);
    }

    [Fact]
    public void MinimumSizeKeepsAspect()
    {
        var bounds = new Rectangle(0, 0, 100, 100);

        Rectangle result = AspectSizing.Constrain(bounds, SizingEdge.Right, PortraitAspect, 160, 90);

        Assert.Equal(160, result.Width);
        Assert.Equal(284, result.Height);
    }

    [Fact]
    public void InvalidAspectReturnsOriginalBounds()
    {
        var bounds = new Rectangle(10, 20, 300, 200);

        Rectangle result = AspectSizing.Constrain(bounds, SizingEdge.Right, 0, 160, 90);

        Assert.Equal(bounds, result);
    }
}
