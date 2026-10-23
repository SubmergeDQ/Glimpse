namespace Glimpse.Diagnostics;

/// <summary>
/// 面板与窗口内读数共用的实时指标，名称与每秒日志字段一一对应：
/// cap/show/age p95/present p95/drop。
/// </summary>
internal readonly record struct LiveStats(
    double CaptureFps,
    double ShowFps,
    double AgeP95,
    double PresentP95,
    int Dropped,
    DateTime? LastFrameAt);

/// <summary>
/// 每秒统计与界面读数之间的单向通道。
/// 统计线程只写、UI 线程只读；读数不参与采样，避免和每秒日志争抢滑动窗口。
/// </summary>
internal sealed class LiveStatsChannel
{
    private readonly object _gate = new();
    private LiveStats _latest = new(0, 0, 0, 0, 0, null);

    public void Publish(
        double captureFps,
        double showFps,
        double ageP95,
        double presentP95,
        int dropped,
        bool hasFrames)
    {
        lock (_gate)
        {
            DateTime? lastFrameAt = hasFrames ? DateTime.Now : _latest.LastFrameAt;
            _latest = new LiveStats(captureFps, showFps, ageP95, presentP95, dropped, lastFrameAt);
        }
    }

    public LiveStats Read()
    {
        lock (_gate)
        {
            return _latest;
        }
    }
}
