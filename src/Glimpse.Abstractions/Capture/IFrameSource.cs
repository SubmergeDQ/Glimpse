namespace Glimpse.Abstractions.Capture;

/// <summary>
/// 帧源：把一块屏幕的画面变成 GPU 纹理。
/// 本机捕获与未来的跨设备接收都实现该接口，显示端不需要知道画面来自哪里。
/// </summary>
public interface IFrameSource : IDisposable
{
    /// <summary>源画面尺寸，用于显示端等比排版。</summary>
    (int Width, int Height) FrameSize { get; }

    /// <summary>
    /// 取走当前最新帧，帧源内部不保留队列。
    /// 返回 <c>false</c> 表示自上次取帧以来没有新内容，调用方应继续显示上一帧而不是清空画面。
    /// </summary>
    bool TryTakeLatestFrame(out CapturedFrame frame);

    /// <summary>开始捕获。重复调用应幂等。</summary>
    void StartCapture();

    /// <summary>停止捕获但保留资源，便于显示器切换后重新开始。</summary>
    void StopCapture();
}
