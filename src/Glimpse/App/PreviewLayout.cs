using Glimpse.Abstractions.Display;

namespace Glimpse.App;

/// <summary>
/// 首次启动时按源画面比例推导预览窗位置：目标屏右下角，约占屏幕一半。
/// 全部使用物理像素。
/// </summary>
internal static class PreviewLayout
{
    private const int Margin = 24;

    public static (int X, int Y, int Width, int Height) Compute(
        MonitorDescriptor monitor,
        int sourceWidth,
        int sourceHeight)
    {
        int safeSourceWidth = Math.Max(1, sourceWidth);
        int safeSourceHeight = Math.Max(1, sourceHeight);

        int budgetWidth = Math.Max(160, (int)(monitor.Width * 0.45));
        int budgetHeight = Math.Max(90, (int)(monitor.Height * 0.55));

        double scale = Math.Min(
            budgetWidth / (double)safeSourceWidth,
            budgetHeight / (double)safeSourceHeight);

        int width = Math.Max(160, (int)Math.Round(safeSourceWidth * scale));
        int height = Math.Max(90, (int)Math.Round(safeSourceHeight * scale));

        int maxWidth = Math.Max(160, monitor.Width - (Margin * 2));
        int maxHeight = Math.Max(90, monitor.Height - (Margin * 2));
        width = Math.Min(width, maxWidth);
        height = Math.Min(height, maxHeight);

        return (
            monitor.X + monitor.Width - width - Margin,
            monitor.Y + monitor.Height - height - Margin,
            width,
            height);
    }
}
