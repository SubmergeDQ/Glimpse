namespace Glimpse.Diagnostics;

/// <summary>两次上报之间发生的帧数。</summary>
internal readonly record struct FrameStatsSnapshot(int Captured, int Presented, int Dropped);

/// <summary>
/// 捕获、呈现、丢弃计数。每秒取窗一次，窗口值即为该秒的帧率。
/// </summary>
internal sealed class FrameStats
{
    private long _captured;
    private long _presented;
    private long _dropped;

    private long _totalCaptured;
    private long _totalPresented;
    private long _totalDropped;

    public long TotalCaptured => Interlocked.Read(ref _totalCaptured);

    public long TotalPresented => Interlocked.Read(ref _totalPresented);

    public long TotalDropped => Interlocked.Read(ref _totalDropped);

    public void RecordCaptured()
    {
        Interlocked.Increment(ref _captured);
        Interlocked.Increment(ref _totalCaptured);
    }

    public void RecordPresented()
    {
        Interlocked.Increment(ref _presented);
        Interlocked.Increment(ref _totalPresented);
    }

    public void RecordDropped()
    {
        Interlocked.Increment(ref _dropped);
        Interlocked.Increment(ref _totalDropped);
    }

    public FrameStatsSnapshot TakeWindow() =>
        new(
            (int)Interlocked.Exchange(ref _captured, 0),
            (int)Interlocked.Exchange(ref _presented, 0),
            (int)Interlocked.Exchange(ref _dropped, 0));
}
