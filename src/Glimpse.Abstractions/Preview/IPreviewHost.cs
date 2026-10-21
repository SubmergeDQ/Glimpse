using Glimpse.Abstractions.Capture;

namespace Glimpse.Abstractions.Preview;

/// <summary>
/// 预览显示端：把帧纹理呈现到窗口。只负责显示，不负责采集。
/// </summary>
public interface IPreviewHost : IDisposable
{
    /// <summary>
    /// 点击穿透。开启后鼠标事件应直接落到下层窗口，
    /// 避免预览窗抢走舞台控制软件的前台焦点。
    /// </summary>
    bool IsClickThrough { get; set; }

    /// <summary>呈现一帧。实现不得阻塞采集线程，未消费的帧应被丢弃而不是排队。</summary>
    void Present(in CapturedFrame frame);

    /// <summary>窗口客户区尺寸变化时调用，内部走交换链 ResizeBuffers 而不是重建整条链路。</summary>
    void Resize(int width, int height);

    void Show();
}
