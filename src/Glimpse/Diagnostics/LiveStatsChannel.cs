namespace Glimpse.Diagnostics;

/// <summary>窗口内读数需要的实时指标。</summary>
internal readonly record struct LiveStats(double ShowFps, double PresentP95, DateTime? LastFrameAt);

/// <summary>
/// 每秒统计与界面读数之间的单向通道。
/// 统计线程只写、UI 线程只读；读数不参与采样，避免和每秒日志争抢滑动窗口。
/// </summary>
internal sealed class LiveStatsChannel
{
    private readonly object _gate = new();
    private LiveStats _latest = new(0, 0, null);

    public void Publish(double showFps, double presentP95, bool hasFrames)
    {
        lock (_gate)
        {
            DateTime? lastFrameAt = hasFrames ? DateTime.Now : _latest.LastFrameAt;
            _latest = new LiveStats(showFps, presentP95, lastFrameAt);
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
