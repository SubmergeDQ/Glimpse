using Vortice.Mathematics;

namespace Glimpse.Render;

/// <summary>
/// 等比缩放视口计算。纯函数，不依赖 GPU，可直接单元测试。
/// 剩余空间由调用方清成黑色。
/// </summary>
internal static class AspectFit
{
    public static Viewport Compute(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            return new Viewport(0, 0, 1, 1, 0, 1);
        }

        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return new Viewport(0, 0, targetWidth, targetHeight, 0, 1);
        }

        double scale = Math.Min(
            targetWidth / (double)sourceWidth,
            targetHeight / (double)sourceHeight);

        int width = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, targetWidth);
        int height = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, targetHeight);

        return new Viewport(
            (targetWidth - width) / 2,
            (targetHeight - height) / 2,
            width,
            height,
            0,
            1);
    }
}
