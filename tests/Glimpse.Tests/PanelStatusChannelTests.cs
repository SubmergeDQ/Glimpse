using Glimpse.Diagnostics;
using Xunit;

namespace Glimpse.Tests;

public sealed class PanelStatusChannelTests
{
    [Fact]
    public void PublishKeepsLatestStatus()
    {
        var channel = new PanelStatusChannel();
        channel.Publish(PanelStatus.Switching("正在切换到 #1…"));
        channel.Publish(PanelStatus.Error("切换失败", "设备忙"));

        PanelStatus status = channel.Read();

        Assert.Equal(PanelStatusKind.Error, status.Kind);
        Assert.Equal("切换失败", status.Headline);
        Assert.Equal("设备忙", status.Detail);
    }

    [Fact]
    public void OkStatusHasNoDetail()
    {
        PanelStatus status = PanelStatus.Ok("正在监看 #0");

        Assert.Equal(PanelStatusKind.Ok, status.Kind);
        Assert.Null(status.Detail);
    }
}
