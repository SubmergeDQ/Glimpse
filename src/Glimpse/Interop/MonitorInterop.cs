using System.Runtime.InteropServices;
using Glimpse.Abstractions.Display;

namespace Glimpse.Interop;

/// <summary>
/// 显示器枚举。坐标全部来自 Win32，单位是物理像素；
/// 不得与 WinForms 的 <c>Screen.Bounds</c>（DIP）混用。
/// </summary>
internal static class MonitorInterop
{
    private const uint MonitorInfoFlagPrimary = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayMonitors", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr dc,
        IntPtr clipRect,
        MonitorEnumProc callback,
        IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>枚举全部显示器，按 X 再按 Y 排序后赋予稳定序号。</summary>
    public static List<MonitorDescriptor> Enumerate()
    {
        var raw = new List<(IntPtr Handle, bool IsPrimary, int X, int Y, int Width, int Height)>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref NativeRect _, IntPtr _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(hMonitor, ref info))
            {
                return true;
            }

            raw.Add((
                hMonitor,
                (info.Flags & MonitorInfoFlagPrimary) != 0,
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left,
                info.Monitor.Bottom - info.Monitor.Top));

            return true;
        }, IntPtr.Zero);

        // 排序后再赋序号，保证同一台机器上每次启动的编号一致
        return raw
            .OrderBy(item => item.X)
            .ThenBy(item => item.Y)
            .Select((item, index) => new MonitorDescriptor(
                item.Handle,
                index,
                item.IsPrimary,
                item.X,
                item.Y,
                item.Width,
                item.Height))
            .ToList();
    }
}
