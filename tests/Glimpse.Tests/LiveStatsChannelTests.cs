using Glimpse.Diagnostics;
using Xunit;

namespace Glimpse.Tests;

public sealed class LiveStatsChannelTests
{
    [Fact]
    public void PublishThenReadReturnsLatest()
    {
        var channel = new LiveStatsChannel();
        channel.Publish(58, 56, 12.5, 17.7, 1, hasFrames: true);

        LiveStats stats = channel.Read();

        Assert.Equal(58, stats.CaptureFps);
        Assert.Equal(56, stats.ShowFps);
        Assert.Equal(12.5, stats.AgeP95);
        Assert.Equal(17.7, stats.PresentP95);
        Assert.Equal(1, stats.Dropped);
        Assert.NotNull(stats.LastFrameAt);
    }

    [Fact]
    public void NoFramesKeepsPreviousLastFrameTime()
    {
        var channel = new LiveStatsChannel();
        channel.Publish(58, 56, 12.5, 17.7, 0, hasFrames: true);
        DateTime? first = channel.Read().LastFrameAt;

        channel.Publish(0, 0, 0, 0, 0, hasFrames: false);
        LiveStats stats = channel.Read();

        Assert.Equal(first, stats.LastFrameAt);
        Assert.Equal(0, stats.ShowFps);
    }

    [Fact]
    public void EmptyChannelHasNoFrameTime()
    {
        Assert.Null(new LiveStatsChannel().Read().LastFrameAt);
    }
}
