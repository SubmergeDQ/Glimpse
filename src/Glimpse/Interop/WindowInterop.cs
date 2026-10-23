using System.Runtime.InteropServices;

namespace Glimpse.Interop;

/// <summary>
/// 窗口样式与系统集成相关的原生调用。
/// 这些常量只在直接操作 HWND 时使用，不进入 UI 层。
/// </summary>
internal static class WindowInterop
{
    public const long WsExTransparent = 0x00000020;
    public const long WsExToolWindow = 0x00000080;
    public const long WsExNoRedirectionBitmap = 0x00200000;
    public const long WsExNoActivate = 0x08000000;

    public const int WdaExcludeFromCapture = 0x11;

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModNoRepeat = 0x4000;

    private const int GwlExStyle = -20;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;

    private static readonly IntPtr HwndTopMost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "SetWindowDisplayAffinity", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "DestroyIcon", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    /// <summary>
    /// 切换点击穿透。改完扩展样式必须带 SWP_FRAMECHANGED 才会生效。
    /// 注意：带 WS_EX_NOREDIRECTIONBITMAP 的合成窗口上此样式不会阻断命中测试，
    /// 真正的穿透需要改用普通窗口呈现路径，见开发文档风险清单。
    /// </summary>
    public static void SetClickThrough(IntPtr hwnd, bool enabled)
    {
        long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        long updated = enabled ? style | WsExTransparent : style & ~WsExTransparent;
        if (updated == style)
        {
            return;
        }

        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(updated));
        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    /// <summary>隐藏窗口内容，使其不出现在任何屏幕捕获结果中。</summary>
    public static bool SetCaptureExclusion(IntPtr hwnd) =>
        SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);

    /// <summary>按物理像素设置窗口位置与尺寸，并保持置顶。</summary>
    public static void PlaceWindow(IntPtr hwnd, int x, int y, int width, int height)
    {
        SetWindowPos(hwnd, HwndTopMost, x, y, width, height, SwpNoActivate);
    }

    /// <summary>
    /// 只移动与缩放，不改变 Z 序。用于换源/换屏等运行期调整，
    /// 避免预览窗被抬到正在操作的面板之上。
    /// </summary>
    public static void MoveWindow(IntPtr hwnd, int x, int y, int width, int height)
    {
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SwpNoActivate | SwpNoZOrder);
    }

    /// <summary>客户端区尺寸，单位物理像素。</summary>
    public static (int Width, int Height) GetClientSize(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out NativeRect rect))
        {
            return (0, 0);
        }

        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>窗口在虚拟桌面中的位置与尺寸，单位物理像素。</summary>
    public static (int X, int Y, int Width, int Height) GetWindowBounds(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out NativeRect rect))
        {
            return (0, 0, 0, 0);
        }

        return (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>窗口所在显示器的有效 DPI，96 表示 100%。句柄无效时按 96 处理。</summary>
    public static int GetDpi(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 96 : (int)dpi;
    }

    /// <summary>读取 WS_EX_TOPMOST，用于确认窗口是否真的置顶。</summary>
    public static bool IsTopMost(IntPtr hwnd) =>
        (GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & 0x8) != 0;
}
