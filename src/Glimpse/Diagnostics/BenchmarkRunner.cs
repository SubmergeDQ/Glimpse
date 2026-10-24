using System.Diagnostics;

namespace Glimpse.Diagnostics;

/// <summary>一次基准测试的汇总结果，字段与第 8 节的验收指标一一对应。</summary>
internal sealed record BenchmarkResult(
    DateTime StartedAt,
    TimeSpan Duration,
    long Captured,
    long Presented,
    long Dropped,
    double CaptureFps,
    double PresentFps,
    LatencySnapshot Age,
    LatencySnapshot Present,
    double CpuPercentSingleCore);

/// <summary>
/// 基准测试：在指定时长内收集捕获/呈现帧数、全程延迟分位数与 CPU 占用。
/// 不改变采集或呈现行为，只做观测。
/// </summary>
internal sealed class BenchmarkRunner
{
    private readonly FrameStats _stats;
    private readonly LatencyMonitor _captureLatency;
    private readonly LatencyMonitor _presentLatency;
    private readonly Process _process = Process.GetCurrentProcess();

    private bool _running;
    private CancellationTokenSource? _cancellation;

    public BenchmarkRunner(FrameStats stats, LatencyMonitor captureLatency, LatencyMonitor presentLatency)
    {
        _stats = stats;
        _captureLatency = captureLatency;
        _presentLatency = presentLatency;
    }

    public bool IsRunning => _running;

    /// <summary>提前结束当前测试，用已采集数据结算并照常返回结果。</summary>
    public void Stop() => _cancellation?.Cancel();

    public async Task<BenchmarkResult> RunAsync(TimeSpan duration)
    {
        if (_running)
        {
            throw new InvalidOperationException("基准测试已在进行中。");
        }

        _running = true;

        DateTime startedAt = DateTime.Now;
        long capturedBefore = _stats.TotalCaptured;
        long presentedBefore = _stats.TotalPresented;
        long droppedBefore = _stats.TotalDropped;
        TimeSpan cpuBefore = _process.TotalProcessorTime;

        _captureLatency.BeginSession();
        _presentLatency.BeginSession();

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await Task.Delay(duration, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户停止测试：不丢弃已采集数据，按实际时长结算
        }
        finally
        {
            stopwatch.Stop();
            _cancellation = null;
            _running = false;
        }

        LatencySnapshot age = _captureLatency.EndSession();
        LatencySnapshot present = _presentLatency.EndSession();

        long captured = _stats.TotalCaptured - capturedBefore;
        long presented = _stats.TotalPresented - presentedBefore;
        long dropped = _stats.TotalDropped - droppedBefore;

        TimeSpan actual = stopwatch.Elapsed;
        double seconds = Math.Max(1.0, actual.TotalSeconds);
        double cpuPercent = (_process.TotalProcessorTime - cpuBefore).TotalSeconds / seconds * 100.0;

        return new BenchmarkResult(
            startedAt,
            actual,
            captured,
            presented,
            dropped,
            captured / seconds,
            presented / seconds,
            age,
            present,
            cpuPercent);
    }
}
