namespace Glimpse.Diagnostics;

/// <summary>一次性统计结果。窗口内没有样本时 <see cref="Count"/> 为 0。</summary>
internal readonly record struct LatencySnapshot(int Count, double Avg, double P95, double Max)
{
    public static LatencySnapshot Empty { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// 固定容量的滑动窗口延迟统计。
/// 采集线程写、统计线程每秒取走一次，两者通过短锁串行化，临界区仅做数组写入。
/// </summary>
internal sealed class LatencyMonitor
{
    private readonly object _gate = new();
    private readonly double[] _samples;
    private readonly List<double> _sessionSamples = new();
    private int _count;
    private int _next;
    private bool _collectSession;

    public LatencyMonitor(int capacity = 600)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _samples = new double[capacity];
    }

    /// <summary>开始为基准测试收集全程样本；不影响每秒滑动窗口。</summary>
    public void BeginSession()
    {
        lock (_gate)
        {
            _sessionSamples.Clear();
            _collectSession = true;
        }
    }

    /// <summary>结束收集并返回全程分位数，之后恢复正常统计。</summary>
    public LatencySnapshot EndSession()
    {
        double[] session;
        lock (_gate)
        {
            _collectSession = false;
            session = _sessionSamples.ToArray();
            _sessionSamples.Clear();
        }

        return Summarize(session);
    }

    public void Record(double milliseconds)
    {
        // 负值通常来自时间戳与当前时刻不同源，直接丢弃以免污染统计
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
        {
            return;
        }

        lock (_gate)
        {
            _samples[_next] = milliseconds;
            _next = (_next + 1) % _samples.Length;
            if (_count < _samples.Length)
            {
                _count++;
            }

            if (_collectSession)
            {
                _sessionSamples.Add(milliseconds);
            }
        }
    }

    /// <summary>取走当前窗口并清空，下一次统计从零开始。</summary>
    public LatencySnapshot TakeSnapshot()
    {
        double[] snapshot;
        lock (_gate)
        {
            snapshot = _count == 0
                ? []
                : _count < _samples.Length
                    ? _samples[.._count]
                    : (double[])_samples.Clone();

            _count = 0;
            _next = 0;
        }

        return Summarize(snapshot);
    }

    /// <summary>纯函数：排序后按最近秩法取分位数，便于单元测试。</summary>
    internal static LatencySnapshot Summarize(double[] samples)
    {
        if (samples.Length == 0)
        {
            return LatencySnapshot.Empty;
        }

        Array.Sort(samples);
        double sum = 0;
        foreach (double value in samples)
        {
            sum += value;
        }

        return new LatencySnapshot(
            samples.Length,
            sum / samples.Length,
            Percentile(samples, 0.95),
            samples[^1]);
    }

    /// <summary>最近秩法：index = ceil(p * n) - 1。</summary>
    internal static double Percentile(double[] sortedSamples, double percentile)
    {
        if (sortedSamples.Length == 0)
        {
            return 0;
        }

        if (percentile <= 0)
        {
            return sortedSamples[0];
        }

        if (percentile >= 1)
        {
            return sortedSamples[^1];
        }

        int index = (int)Math.Ceiling(percentile * sortedSamples.Length) - 1;
        return sortedSamples[Math.Clamp(index, 0, sortedSamples.Length - 1)];
    }
}
