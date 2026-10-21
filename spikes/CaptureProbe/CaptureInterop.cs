using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace CaptureProbe;

/// <summary>
/// 显示器枚举与 WGC 捕获项创建。
/// 这里刻意使用 Win32 + COM interop，而不是依赖更高层封装，便于确认底层行为。
/// </summary>
internal static class CaptureInterop
{
    private const string GraphicsCaptureItemClassId = "Windows.Graphics.Capture.GraphicsCaptureItem";

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr item);

        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr item);
    }

    public sealed record MonitorTarget(IntPtr Handle, int Index, bool IsPrimary, int X, int Y, int Width, int Height)
    {
        public override string ToString() =>
            $"#{Index} {(IsPrimary ? "[主屏] " : "[副屏] ")}{Width}x{Height} @ ({X},{Y})";
    }

    public static List<MonitorTarget> EnumerateMonitors()
    {
        var monitors = new List<MonitorTarget>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hmon, IntPtr _, ref Rect _, IntPtr _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(hmon, ref info))
            {
                return true;
            }

            bool primary = (info.Flags & 1) != 0;
            monitors.Add(new MonitorTarget(
                hmon,
                monitors.Count,
                primary,
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left,
                info.Monitor.Bottom - info.Monitor.Top));
            return true;
        }, IntPtr.Zero);

        return monitors.OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
    }

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        int hr = WindowsCreateString(GraphicsCaptureItemClassId, GraphicsCaptureItemClassId.Length, out IntPtr classId);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            Guid interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            hr = RoGetActivationFactory(classId, ref interopIid, out IntPtr factoryPtr);
            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
                Guid itemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                int createResult = interop.CreateForMonitor(monitor, ref itemIid, out IntPtr itemPtr);
                if (createResult < 0)
                {
                    Marshal.ThrowExceptionForHR(createResult);
                }

                if (itemPtr == IntPtr.Zero)
                {
                    throw new InvalidOperationException("CreateForMonitor 返回空指针");
                }

                try
                {
                    // 交给 CsWinRT 转换成强类型对象，之后再释放原始引用
                    return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
                }
                finally
                {
                    Marshal.Release(itemPtr);
                }
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            WindowsDeleteString(classId);
        }
    }

    public static double QueryPerfCounterSeconds()
    {
        QueryPerformanceCounter(out long counter);
        QueryPerformanceFrequency(out long frequency);
        return counter / (double)frequency;
    }

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceCounter(out long value);

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceFrequency(out long value);
}
