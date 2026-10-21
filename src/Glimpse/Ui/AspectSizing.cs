namespace Glimpse.Ui;

/// <summary>WM_SIZING 报告的拖动边或角。</summary>
internal enum SizingEdge
{
    Left = 1,
    Right = 2,
    Top = 3,
    TopLeft = 4,
    TopRight = 5,
    Bottom = 6,
    BottomLeft = 7,
    BottomRight = 8,
}

/// <summary>
/// 交互缩放时的宽高比约束。纯函数，便于单元测试。
/// 窗口为无边框窗口，窗口矩形与客户区一致，因此直接把窗口比例当作画面比例。
/// </summary>
internal static class AspectSizing
{
    public static Rectangle Constrain(
        Rectangle bounds,
        SizingEdge edge,
        double aspect,
        int minWidth,
        int minHeight)
    {
        if (aspect <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return bounds;
        }

        int width = bounds.Width;
        int height = bounds.Height;

        switch (edge)
        {
            case SizingEdge.Left:
            case SizingEdge.Right:
                // 横向拖动决定宽度，高度由比例推导
                height = (int)Math.Round(width / aspect);
                break;

            case SizingEdge.Top:
            case SizingEdge.Bottom:
                // 纵向拖动决定高度，宽度由比例推导
                width = (int)Math.Round(height * aspect);
                break;

            default:
                // 拖角时沿用用户拖动幅度更大的那一维，避免窗口突然缩小
                int heightFromWidth = (int)Math.Round(width / aspect);
                if (heightFromWidth >= height)
                {
                    height = heightFromWidth;
                }
                else
                {
                    width = (int)Math.Round(height * aspect);
                }

                break;
        }

        (width, height) = EnforceMinimum(width, height, aspect, minWidth, minHeight);

        // 固定与拖动边相对的另一侧，让窗口按用户预期方向生长
        int left = edge is SizingEdge.Left or SizingEdge.TopLeft or SizingEdge.BottomLeft
            ? bounds.Right - width
            : bounds.Left;
        int top = edge is SizingEdge.Top or SizingEdge.TopLeft or SizingEdge.TopRight
            ? bounds.Bottom - height
            : bounds.Top;

        return new Rectangle(left, top, width, height);
    }

    private static (int Width, int Height) EnforceMinimum(
        int width,
        int height,
        double aspect,
        int minWidth,
        int minHeight)
    {
        minWidth = Math.Max(1, minWidth);
        minHeight = Math.Max(1, minHeight);

        if (width < minWidth)
        {
            width = minWidth;
            height = Math.Max(minHeight, (int)Math.Round(width / aspect));
        }

        if (height < minHeight)
        {
            height = minHeight;
            width = Math.Max(minWidth, (int)Math.Round(height * aspect));
        }

        return (width, height);
    }
}
