using Glimpse.Abstractions.Display;
using Glimpse.App;
using Xunit;

namespace Glimpse.Tests;

public sealed class PreviewLayoutTests
{
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
}
