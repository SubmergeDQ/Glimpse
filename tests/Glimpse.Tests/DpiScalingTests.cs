using Glimpse.Ui;
using Xunit;

namespace Glimpse.Tests;

public sealed class DpiScalingTests
{
    [Fact]
    public void ScaleAtDefaultDpiKeepsLogicalPixels()
    {
        Assert.Equal(8, DpiScaling.Scale(8, 96));
        Assert.Equal(154, DpiScaling.Scale(154, 96));
    }

    [Fact]
    public void ScaleAt125PercentRoundsToPhysicalPixels()
    {
        Assert.Equal(10, DpiScaling.Scale(8, 120));
        Assert.Equal(16, DpiScaling.Scale(13, 120));
    }

    [Fact]
    public void ScaleAt150PercentMatchesMonitorScale()
    {
        Assert.Equal(12, DpiScaling.Scale(8, 144));
        Assert.Equal(231, DpiScaling.Scale(154, 144));
        Assert.Equal(72, DpiScaling.Scale(48, 144));
    }

    [Fact]
    public void ScaleAt200PercentDoublesLogicalPixels()
    {
        Assert.Equal(160, DpiScaling.Scale(80, 192));
        Assert.Equal(308, DpiScaling.Scale(154, 192));
        Assert.Equal(12, DpiScaling.Scale(6, 192));
    }

    [Fact]
    public void InvalidDpiFallsBackToDefault()
    {
        Assert.Equal(8, DpiScaling.Scale(8, 0));
        Assert.Equal(8, DpiScaling.Scale(8, -1));
    }

    [Fact]
    public void ScaleNeverReturnsZero()
    {
        Assert.Equal(1, DpiScaling.Scale(0, 96));
    }
}
