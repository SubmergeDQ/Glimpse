using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Glimpse.Interop;

/// <summary>
/// QueryPerformanceCounter 原生调用。
/// 与 WGC 的 <c>SystemRelativeTime</c> 同源，是帧年龄与端到端统计的唯一时间基准。
/// </summary>
internal static class PerformanceCounterInterop
{
    static PerformanceCounterInterop()
    {
        QueryPerformanceFrequency(out long frequency);
        Frequency = frequency > 0 ? frequency : Stopwatch.Frequency;
    }

    public static long Frequency { get; }

    public static long QueryCounter()
    {
        QueryPerformanceCounter(out long counter);
        return counter;
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryPerformanceCounter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryPerformanceCounter(out long value);

    [DllImport("kernel32.dll", EntryPoint = "QueryPerformanceFrequency", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryPerformanceFrequency(out long value);
}
