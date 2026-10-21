namespace Glimpse.Abstractions.Diagnostics;

/// <summary>
/// 单调时间源。
/// 帧时间戳与测量时刻必须来自同一基准，否则会出现负延迟这类不可信读数。
/// </summary>
public interface IFrameClock
{
    /// <summary>自系统启动起的秒数，精度与 QueryPerformanceCounter 同级。</summary>
    double NowSeconds { get; }
}
