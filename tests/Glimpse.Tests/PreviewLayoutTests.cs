using Glimpse.Abstractions.Configuration;
using Glimpse.Abstractions.Display;
using Glimpse.App;
using Xunit;

namespace Glimpse.Tests;

public sealed class PreviewLayoutTests
{
    private static readonly MonitorDescriptor Primary =
        new(IntPtr.Zero, 0, true, 0, 0, 2560, 1440, @"\\.\DISPLAY1");

    private static readonly MonitorDescriptor Secondary =
        new(new IntPtr(1), 1, false, 2560, 0, 1080, 1920, @"\\.\DISPLAY2");

    private static readonly MonitorDescriptor[] Monitors = [Primary, Secondary];

    [Fact]
    public void ComputeKeepsSourceAspectAndStaysInsideMonitor()
    {
        var monitor = new MonitorDescriptor(IntPtr.Zero, 0, true, 0, 0, 2560, 1440);

        (int x, int y, int width, int height) = PreviewLayout.Compute(monitor, 1080, 1920);

        Assert.True(x >= monitor.X);
        Assert.True(y >= monitor.Y);
        Assert.True(x + width <= monitor.X + monitor.Width);
        Assert.True(y + height <= monitor.Y + monitor.Height);

        double expectedAspect = 1080.0 / 1920.0;
        Assert.Equal(expectedAspect, width / (double)height, 2);
    }

    [Fact]
    public void ComputePlacesWindowAtBottomRight()
    {
        var monitor = new MonitorDescriptor(IntPtr.Zero, 0, true, 0, 0, 2560, 1440);

        (int x, int y, int width, int height) = PreviewLayout.Compute(monitor, 1920, 1080);

        Assert.Equal(2560 - width - 24, x);
        Assert.Equal(1440 - height - 24, y);
    }

    [Fact]
    public void IsUsableAcceptsPlacementOnAnyMonitor()
    {
        // 中心点落在副屏区域内
        Assert.True(PreviewLayout.IsUsable(new PreviewPlacement(3000, 100, 400, 800), Monitors));
    }

    [Fact]
    public void IsUsableRejectsPlacementOutsideVirtualDesktop()
    {
        Assert.False(PreviewLayout.IsUsable(new PreviewPlacement(5000, 100, 400, 800), Monitors));
    }

    [Fact]
    public void IsUsableRejectsTooSmallPlacement()
    {
        Assert.False(PreviewLayout.IsUsable(new PreviewPlacement(100, 100, 10, 10), Monitors));
    }

    [Fact]
    public void FitToAspectKeepsMatchingSize()
    {
        (int width, int height) = PreviewLayout.FitToAspect(540, 960, 1080, 1920);

        Assert.Equal(540, width);
        Assert.Equal(960, height);
    }

    [Fact]
    public void FitToAspectShrinksWidthWhenTooWide()
    {
        (int width, int height) = PreviewLayout.FitToAspect(1000, 500, 1080, 1920);

        Assert.Equal(281, width);
        Assert.Equal(500, height);
    }

    [Fact]
    public void FitToMonitorClampsWindowInsidePreviewMonitor()
    {
        // 窗口原位置在右下角之外，换源后应被收敛回屏内
        (int x, int y, int width, int height) = PreviewLayout.FitToMonitor(
            2500,
            1400,
            400,
            600,
            1920,
            1080,
            Primary);

        Assert.True(x >= Primary.X);
        Assert.True(y >= Primary.Y);
        Assert.True(x + width <= Primary.X + Primary.Width);
        Assert.True(y + height <= Primary.Y + Primary.Height);
        Assert.Equal(1920.0 / 1080.0, width / (double)height, 1);
    }

    [Fact]
    public void FitToMonitorKeepsWindowCenter()
    {
        (int x, int y, int width, int height) = PreviewLayout.FitToMonitor(
            1000,
            600,
            400,
            600,
            2560,
            1440,
            Primary);

        Assert.InRange(x + (width / 2), 1190, 1210);
        Assert.InRange(y + (height / 2), 890, 910);
    }

    [Fact]
    public void FitToAspectPreservingAreaIsReversible()
    {
        // 竖屏 → 横屏 → 竖屏，尺寸应基本回到原值而不是逐次缩小
        (int landscapeWidth, int landscapeHeight) =
            PreviewLayout.FitToAspectPreservingArea(446, 792, 2560, 1440);
        (int portraitWidth, int portraitHeight) =
            PreviewLayout.FitToAspectPreservingArea(landscapeWidth, landscapeHeight, 1080, 1920);

        Assert.InRange(portraitWidth, 444, 448);
        Assert.InRange(portraitHeight, 790, 794);
    }

    [Fact]
    public void FitToMonitorShrinksOversizedWindow()
    {
        (int _, int _, int width, int height) = PreviewLayout.FitToMonitor(
            0,
            0,
            4000,
            4000,
            1000,
            1000,
            Primary);

        Assert.True(width <= Primary.Width);
        Assert.True(height <= Primary.Height);
    }
}
