namespace Glimpse.Ui;

/// <summary>
/// DPI 换算。界面常量按 96 DPI 的逻辑像素书写，只在需要物理像素时经这里换算，
/// 避免把某一屏的缩放比例带进其他显示器的几何计算。
/// </summary>
internal static class DpiScaling
{
    public const int DefaultDpi = 96;

    /// <summary>把 96 DPI 下的逻辑像素换算成目标 DPI 的物理像素，最小为 1。</summary>
    public static int Scale(int logicalPixels, int dpi)
    {
        if (dpi <= 0)
        {
            dpi = DefaultDpi;
        }

        return Math.Max(1, (int)Math.Round(logicalPixels * dpi / (double)DefaultDpi));
    }
}
