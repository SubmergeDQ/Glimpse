using Glimpse.Abstractions.Configuration;
using Glimpse.Abstractions.Display;

namespace Glimpse.App;

/// <summary>
/// 首次启动时按源画面比例推导预览窗位置：目标屏右下角，约占屏幕一半。
/// 全部使用物理像素。
/// </summary>
internal static class PreviewLayout
{
    private const int Margin = 24;
    private const int MinimumWidth = 160;
    private const int MinimumHeight = 90;

    /// <summary>
    /// 判断保存的位置是否仍可用：尺寸有效，且窗口中心落在某块当前显示器内。
    /// 显示器被拔掉或重新排列后，旧坐标可能落在虚拟桌面之外。
    /// </summary>
    public static bool IsUsable(PreviewPlacement placement, IReadOnlyList<MonitorDescriptor> monitors)
    {
        if (placement.Width < MinimumWidth || placement.Height < MinimumHeight)
        {
            return false;
        }

        int centerX = placement.X + (placement.Width / 2);
        int centerY = placement.Y + (placement.Height / 2);

        return monitors.Any(monitor =>
            centerX >= monitor.X
            && centerX < monitor.X + monitor.Width
            && centerY >= monitor.Y
            && centerY < monitor.Y + monitor.Height);
    }

    /// <summary>
    /// 恢复窗口尺寸时按当前源画面比例修正，避免源分辨率变化后出现黑边。
    /// 只调整一边，保持左上角位置不变。
    /// </summary>
    public static (int Width, int Height) FitToAspect(
        int width,
        int height,
        int sourceWidth,
        int sourceHeight)
    {
        if (width <= 0 || height <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return (Math.Max(MinimumWidth, width), Math.Max(MinimumHeight, height));
        }

        double targetAspect = sourceWidth / (double)sourceHeight;
        double currentAspect = width / (double)height;
        if (Math.Abs(targetAspect - currentAspect) < 0.01)
        {
            return (width, height);
        }

        if (currentAspect > targetAspect)
        {
            return (Math.Max(MinimumWidth, (int)Math.Round(height * targetAspect)), height);
        }

        return (width, Math.Max(MinimumHeight, (int)Math.Round(width / targetAspect)));
    }

    /// <summary>
    /// 换源时按面积守恒换算尺寸。只按单边换算会让窗口在来回切换中逐次缩小，
    /// 面积守恒保证竖屏 ↔ 横屏往返后尺寸基本回到原值。
    /// </summary>
    public static (int Width, int Height) FitToAspectPreservingArea(
        int width,
        int height,
        int sourceWidth,
        int sourceHeight)
    {
        if (width <= 0 || height <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return (Math.Max(MinimumWidth, width), Math.Max(MinimumHeight, height));
        }

        double targetAspect = sourceWidth / (double)sourceHeight;
        double area = width * (double)height;

        int newWidth = Math.Max(MinimumWidth, (int)Math.Round(Math.Sqrt(area * targetAspect)));
        int newHeight = Math.Max(MinimumHeight, (int)Math.Round(newWidth / targetAspect));

        return (newWidth, newHeight);
    }

    /// <summary>
    /// 换源后按新画面比例修正窗口，并保证窗口完全落在预览屏内。
    /// 以窗口中心为锚点缩放，避免左上角固定造成的“越换越往上跑”。
    /// </summary>
    public static (int X, int Y, int Width, int Height) FitToMonitor(
        int x,
        int y,
        int width,
        int height,
        int sourceWidth,
        int sourceHeight,
        MonitorDescriptor monitor)
    {
        (int newWidth, int newHeight) = FitToAspectPreservingArea(
            width,
            height,
            sourceWidth,
            sourceHeight);

        double scale = Math.Min(
            1.0,
            Math.Min(monitor.Width / (double)newWidth, monitor.Height / (double)newHeight));

        if (scale < 1.0)
        {
            newWidth = Math.Max(MinimumWidth, (int)Math.Round(newWidth * scale));
            newHeight = Math.Max(MinimumHeight, (int)Math.Round(newHeight * scale));
        }

        int centerX = x + (width / 2);
        int centerY = y + (height / 2);
        int nextX = centerX - (newWidth / 2);
        int nextY = centerY - (newHeight / 2);

        int maxX = Math.Max(monitor.X, monitor.X + monitor.Width - newWidth);
        int maxY = Math.Max(monitor.Y, monitor.Y + monitor.Height - newHeight);

        return (
            Math.Clamp(nextX, monitor.X, maxX),
            Math.Clamp(nextY, monitor.Y, maxY),
            newWidth,
            newHeight);
    }

    /// <summary>
    /// 100% 尺寸：源画面按比例完整放入目标屏（留 <paramref name="margin"/> 边距）的最大尺寸。
    /// 尺寸档位均以该值为基准，避免不同源分辨率下百分比含义漂移。
    /// </summary>
    public static (int Width, int Height) FitSize(
        MonitorDescriptor monitor,
        int sourceWidth,
        int sourceHeight,
        int margin = Margin)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return (MinimumWidth, MinimumHeight);
        }

        int budgetWidth = Math.Max(1, monitor.Width - (margin * 2));
        int budgetHeight = Math.Max(1, monitor.Height - (margin * 2));
        double scale = Math.Min(
            budgetWidth / (double)sourceWidth,
            budgetHeight / (double)sourceHeight);

        return (
            Math.Max(MinimumWidth, (int)Math.Round(sourceWidth * scale)),
            Math.Max(MinimumHeight, (int)Math.Round(sourceHeight * scale)));
    }

    /// <summary>按贴边方式计算窗口左上角，并保证窗口完整落在目标屏内。</summary>
    public static (int X, int Y) AnchorPosition(
        MonitorDescriptor monitor,
        int width,
        int height,
        PreviewAnchor anchor,
        int margin = Margin)
    {
        int rightX = monitor.X + monitor.Width - width - margin;
        int bottomY = monitor.Y + monitor.Height - height - margin;
        int centerX = monitor.X + ((monitor.Width - width) / 2);
        int centerY = monitor.Y + ((monitor.Height - height) / 2);

        (int x, int y) = anchor switch
        {
            PreviewAnchor.TopRight => (rightX, monitor.Y + margin),
            PreviewAnchor.Center => (centerX, centerY),
            _ => (rightX, bottomY),
        };

        int minX = monitor.X + margin;
        int minY = monitor.Y + margin;
        int maxX = Math.Max(minX, monitor.X + monitor.Width - width - margin);
        int maxY = Math.Max(minY, monitor.Y + monitor.Height - height - margin);

        return (
            Math.Clamp(x, minX, maxX),
            Math.Clamp(y, minY, maxY));
    }

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
