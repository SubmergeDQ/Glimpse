using System.Diagnostics;
using Glimpse.Abstractions.Capture;
using Glimpse.Abstractions.Diagnostics;
using Glimpse.Abstractions.Preview;
using Glimpse.Diagnostics;

namespace Glimpse.App;

/// <summary>
/// 独立渲染线程。只做“取最新帧 → Present”，没有新帧就休眠，
/// 不使用 WinForms Timer，避免消息泵精度影响呈现节拍。
/// </summary>
internal sealed class RenderLoop : IDisposable
{
    // 换屏时由 UI 线程替换；渲染线程每轮重新读取，避免锁住呈现节拍
    private IFrameSource _source;
    private readonly IPreviewHost _host;
    private readonly IFrameClock _clock;
    private readonly LatencyMonitor _presentLatency;
    private readonly StatsReporter _reporter;
    private readonly DiagnosticsLog _log;
    private readonly Func<bool> _canContinue;

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _paused;
    private bool _disposed;

    public RenderLoop(
        IFrameSource source,
        IPreviewHost host,
        IFrameClock clock,
        LatencyMonitor presentLatency,
        StatsReporter reporter,
        DiagnosticsLog log,
        Func<bool> canContinue)
    {
        _source = source;
        _host = host;
        _clock = clock;
        _presentLatency = presentLatency;
        _reporter = reporter;
        _log = log;
        _canContinue = canContinue;
    }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Glimpse.Render",
        };
        _thread.Start();
    }

    /// <summary>切换到新的帧源。旧源由调用方在确认不再被引用后释放。</summary>
    public void SwitchSource(IFrameSource source) => Volatile.Write(ref _source, source);

    /// <summary>暂停呈现但保留最后画面。采集侧通常也会一并停止以省资源。</summary>
    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    private void Loop()
    {
        var reportTimer = Stopwatch.StartNew();

        while (_running && _canContinue())
        {
            try
            {
                if (_paused)
                {
                    // 暂停期间不取帧，窗口保留最后一帧；降低轮询频率避免空转
                    Thread.Sleep(15);
                    continue;
                }

                IFrameSource source = Volatile.Read(ref _source);
                if (source.TryTakeLatestFrame(out CapturedFrame frame))
                {
                    _host.Present(frame);
                    _presentLatency.Record(
                        Math.Max(0, (_clock.NowSeconds - frame.CapturedAtSeconds) * 1000.0));
                }
                else
                {
                    // 静止画面不空转；有新帧时下一轮立即被取走
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                _log.WriteLine($"[渲染] 循环异常，已停止：{ex}");
                _running = false;
            }

            if (reportTimer.ElapsedMilliseconds >= 1000)
            {
                _reporter.Report();
                reportTimer.Restart();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _running = false;

        Thread? thread = _thread;
        _thread = null;

        if (thread is not null && thread.IsAlive && !thread.Join(2000))
        {
            _log.WriteLine("[渲染] 停止超时，线程仍可能存活");
        }
    }
}
