namespace Glimpse.Diagnostics;

/// <summary>
/// 每秒一行诊断输出。格式固定，便于与基线表逐项对照：
/// cap/show 为窗口内帧数，age 为采集时刻到回调时刻，present 为采集时刻到呈现时刻。
/// </summary>
internal sealed class StatsReporter
{
    private readonly FrameStats _stats;
    private readonly LatencyMonitor _captureLatency;
    private readonly LatencyMonitor _presentLatency;
    private readonly DiagnosticsLog _log;

    public StatsReporter(
        FrameStats stats,
        LatencyMonitor captureLatency,
        LatencyMonitor presentLatency,
        DiagnosticsLog log)
    {
        _stats = stats;
        _captureLatency = captureLatency;
        _presentLatency = presentLatency;
        _log = log;
    }

    public void Report()
    {
        FrameStatsSnapshot frames = _stats.TakeWindow();
        LatencySnapshot capture = _captureLatency.TakeSnapshot();
        LatencySnapshot present = _presentLatency.TakeSnapshot();

        _log.WriteLine(
            $"{DateTime.Now:HH:mm:ss} " +
            $"cap={frames.Captured,3}fps show={frames.Presented,3}fps drop={frames.Dropped,2} " +
            $"age(ms) {Format(capture)} " +
            $"present(ms) {Format(present)} " +
            $"total={_stats.TotalCaptured}");
    }

    public void ReportFinal()
    {
        _log.WriteLine(
            $"退出统计：捕获 {_stats.TotalCaptured} 帧，呈现 {_stats.TotalPresented} 帧，" +
            $"丢弃 {_stats.TotalDropped} 帧");
    }

    private static string Format(LatencySnapshot snapshot) =>
        snapshot.Count == 0
            ? "   -/   -/   -"
            : $"{snapshot.Avg,5:F1}/{snapshot.P95,5:F1}/{snapshot.Max,5:F1}";
}
