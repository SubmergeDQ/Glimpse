using Glimpse.Interop;

namespace Glimpse.Platform;

/// <summary>
/// 让预览窗不出现在任何屏幕捕获结果中。
/// 缺少这一步时，预览窗会被自己的 WGC 会话拍进画面，反向预览还会形成递归。
/// </summary>
internal static class WindowCaptureExclusion
{
    /// <summary>
    /// 设置窗口是否从捕获中排除。返回 false 表示系统拒绝该设置，
    /// 常见于低于 Windows 10 2004 的系统不支持 WDA_EXCLUDEFROMCAPTURE。
    /// </summary>
    public static bool Apply(IntPtr hwnd, bool exclude = true) =>
        WindowInterop.SetCaptureExclusion(hwnd, exclude);
}
