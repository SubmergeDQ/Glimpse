using Glimpse.Diagnostics;
using Xunit;

namespace Glimpse.Tests;

public sealed class BenchmarkReportTests
{
    private static readonly BenchmarkResult Sample = new(
        new DateTime(2026, 10, 23, 12, 0, 0),
        TimeSpan.FromSeconds(60),
        3500,
        3490,
        0,
        58.3,
        58.2,
        new LatencySnapshot(3500, 2.0, 5.0, 12.0),
        new LatencySnapshot(3490, 8.0, 12.0, 20.0),
        16.0);

    [Fact]
    public void MarkdownContainsAcceptanceFields()
    {
        string markdown = BenchmarkReport.ToMarkdown(Sample);

        Assert.Contains("捕获：3500 帧（58.3 fps）", markdown);
        Assert.Contains("丢弃：0 帧（0.00%）", markdown);
        Assert.Contains("端到端：avg 8.0 / p95 12.0 / max 20.0 ms", markdown);
        Assert.Contains("CPU：16.0% 单核", markdown);
        Assert.Contains("- 丢弃率：通过", markdown);
        Assert.Contains("- 端到端 p95：通过", markdown);
    }

    [Fact]
    public void MarkdownMarksFailedThresholds()
    {
        var failing = Sample with
        {
            Dropped = 10,
            Present = new LatencySnapshot(100, 30, 45, 60),
            CpuPercentSingleCore = 25,
        };

        string markdown = BenchmarkReport.ToMarkdown(failing);

        Assert.Contains("- 丢弃率：未通过", markdown);
        Assert.Contains("- 端到端 p95：未通过", markdown);
        Assert.Contains("- 端到端 max：未通过", markdown);
        Assert.Contains("- CPU：未通过", markdown);
    }

    [Fact]
    public async Task StopEndsRunEarlyAndStillReturnsResult()
    {
        var runner = new BenchmarkRunner(new FrameStats(), new LatencyMonitor(), new LatencyMonitor());

        Task<BenchmarkResult> run = runner.RunAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(150);
        runner.Stop();

        BenchmarkResult result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        // 提前停止也必须有结果，且实际时长远短于请求的 30 秒
        Assert.True(result.Duration < TimeSpan.FromSeconds(10));
        Assert.False(runner.IsRunning);
    }
}
