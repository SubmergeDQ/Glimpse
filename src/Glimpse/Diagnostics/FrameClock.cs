using Glimpse.Abstractions.Diagnostics;
using Glimpse.Interop;

namespace Glimpse.Diagnostics;

/// <summary>
/// QueryPerformanceCounter 时间源。
/// WGC 的 <c>SystemRelativeTime</c> 与 QPC 同源，两者相减即可得到帧年龄。
/// </summary>
internal sealed class FrameClock : IFrameClock
{
    public double NowSeconds => PerformanceCounterInterop.QueryCounter()
        / (double)PerformanceCounterInterop.Frequency;
}
