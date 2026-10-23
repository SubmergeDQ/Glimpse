using Glimpse.Abstractions.Diagnostics;
using Glimpse.Abstractions.Display;
using Glimpse.Capture;
using Glimpse.Diagnostics;
using Glimpse.Interop;

namespace Glimpse.App;

/// <summary>
/// 捕获会话：持有当前帧源，并支持在不中断渲染线程的前提下切换显示器。
/// 切换时先建立并启动新会话，成功后才停用旧会话；旧源延迟释放，
/// 保证呈现端仍持有最后一帧纹理时不会被提前销毁。
/// </summary>
internal sealed class CaptureSession : IDisposable
{
    private static readonly TimeSpan RetireDelay = TimeSpan.FromSeconds(3);

    private readonly GpuDevice _gpu;
    private readonly IFrameClock _clock;
    private readonly FrameStats _stats;
    private readonly LatencyMonitor _latency;
    private readonly DiagnosticsLog _log;
    private readonly List<RetiredSource> _retired = new();

    private bool _disposed;

    public CaptureSession(
        GpuDevice gpu,
        MonitorDescriptor monitor,
        IFrameClock clock,
        FrameStats stats,
        LatencyMonitor latency,
        DiagnosticsLog log)
    {
        _gpu = gpu;
        _clock = clock;
        _stats = stats;
        _latency = latency;
        _log = log;

        Monitor = monitor;
        Source = Create(monitor);
    }

    public MonitorDescriptor Monitor { get; private set; }

    public WgcFrameSource Source { get; private set; }

    /// <summary>
    /// 切换到新的捕获显示器。返回 <c>false</c> 时旧会话保持运行：
    /// <paramref name="error"/> 为 <c>null</c> 表示目标就是当前显示器，无需切换；
    /// 非空表示新会话创建或启动失败。
    /// </summary>
    public bool TrySwitch(MonitorDescriptor monitor, out string? error)
    {
        error = null;

        if (monitor.Index == Monitor.Index)
        {
            return false;
        }

        WgcFrameSource? candidate = null;
        try
        {
            candidate = Create(monitor);
            candidate.StartCapture();
        }
        catch (Exception ex)
        {
            candidate?.Dispose();
            error = ex.Message;
            return false;
        }

        WgcFrameSource previous = Source;
        previous.StopCapture();

        Source = candidate;
        Monitor = monitor;

        Retire(previous);
        return true;
    }

    private WgcFrameSource Create(MonitorDescriptor monitor) =>
        new(_gpu, monitor, _clock, _stats, _latency, _log);

    private void Retire(WgcFrameSource source)
    {
        var retired = new RetiredSource(source);

        lock (_retired)
        {
            _retired.Add(retired);
        }

        _log.WriteLine($"[采集] 旧会话已停用，{RetireDelay.TotalSeconds:0} 秒后释放");

        retired.Timer = new System.Threading.Timer(
            _ => Release(retired),
            null,
            RetireDelay,
            Timeout.InfiniteTimeSpan);
    }

    private void Release(RetiredSource retired)
    {
        lock (_retired)
        {
            // 已被 Dispose 接管时直接返回，避免重复释放
            if (!_retired.Remove(retired))
            {
                return;
            }
        }

        retired.Timer?.Dispose();
        retired.Source.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        List<RetiredSource> pending;
        lock (_retired)
        {
            pending = new List<RetiredSource>(_retired);
            _retired.Clear();
        }

        foreach (RetiredSource retired in pending)
        {
            retired.Timer?.Dispose();
            retired.Source.Dispose();
        }

        Source.Dispose();
    }

    private sealed class RetiredSource(WgcFrameSource source)
    {
        public WgcFrameSource Source { get; } = source;

        public System.Threading.Timer? Timer { get; set; }
    }
}
