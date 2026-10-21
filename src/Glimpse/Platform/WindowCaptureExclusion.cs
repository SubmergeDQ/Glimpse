using Glimpse.Interop;

namespace Glimpse.Platform;

/// <summary>
/// 让预览窗不出现在任何屏幕捕获结果中。
/// 缺少这一步时，预览窗会被自己的 WGC 会话拍进画面，反向预览还会形成递归。
/// </summary>
internal static class WindowCaptureExclusion
{
    /// <summary>返回 false 表示当前系统不支持 WDA_EXCLUDEFROMCAPTURE。</summary>
    public static bool Apply(IntPtr hwnd) => WindowInterop.SetCaptureExclusion(hwnd);
}
