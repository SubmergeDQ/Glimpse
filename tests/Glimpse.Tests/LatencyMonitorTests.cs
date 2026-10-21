using Glimpse.Diagnostics;
using Xunit;

namespace Glimpse.Tests;

public sealed class LatencyMonitorTests
{
    [Fact]
    public void SummarizeEmptySamplesReturnsEmptySnapshot()
    {
        LatencySnapshot snapshot = LatencyMonitor.Summarize([]);

        Assert.Equal(0, snapshot.Count);
        Assert.Equal(0, snapshot.Avg);
        Assert.Equal(0, snapshot.P95);
        Assert.Equal(0, snapshot.Max);
    }

    [Fact]
    public void SummarizeUsesNearestRankPercentile()
    {
        double[] samples = Enumerable.Range(1, 100).Select(value => (double)value).ToArray();

        LatencySnapshot snapshot = LatencyMonitor.Summarize(samples);

        Assert.Equal(100, snapshot.Count);
        Assert.Equal(50.5, snapshot.Avg, 6);
        Assert.Equal(95, snapshot.P95, 6);
        Assert.Equal(100, snapshot.Max, 6);
    }

    [Fact]
    public void PercentileClampsOutOfRangeValues()
    {
        double[] sorted = [10, 20, 30];

        Assert.Equal(10, LatencyMonitor.Percentile(sorted, -1), 6);
        Assert.Equal(30, LatencyMonitor.Percentile(sorted, 1.5), 6);
    }

    [Fact]
    public void RecordIgnoresInvalidSamples()
    {
        var monitor = new LatencyMonitor(capacity: 4);

        monitor.Record(-1);
        monitor.Record(double.NaN);
        monitor.Record(double.PositiveInfinity);
        monitor.Record(12.5);

        LatencySnapshot snapshot = monitor.TakeSnapshot();

        Assert.Equal(1, snapshot.Count);
        Assert.Equal(12.5, snapshot.Avg, 6);
    }

    [Fact]
    public void TakeSnapshotClearsWindow()
    {
        var monitor = new LatencyMonitor(capacity: 4);
        monitor.Record(5);

        _ = monitor.TakeSnapshot();
        LatencySnapshot second = monitor.TakeSnapshot();

        Assert.Equal(0, second.Count);
    }
}
