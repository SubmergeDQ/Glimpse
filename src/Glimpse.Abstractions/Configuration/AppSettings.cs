namespace Glimpse.Abstractions.Configuration;

/// <summary>
/// 预览窗的位置与尺寸，使用虚拟桌面坐标，允许跨屏摆放。
/// </summary>
public readonly record struct PreviewPlacement(int X, int Y, int Width, int Height);

/// <summary>
/// 应用设置。全部字段可空或带默认值，旧版本配置缺少字段时能直接反序列化。
/// </summary>
public sealed record AppSettings
{
    /// <summary>被预览的源显示器序号，空表示首次启动时自动选择。</summary>
    public int? SourceMonitorIndex { get; init; }

    /// <summary>预览窗所在的显示器序号。</summary>
    public int? PreviewMonitorIndex { get; init; }

    /// <summary>上次关闭时的窗口位置。</summary>
    public PreviewPlacement? Placement { get; init; }

    /// <summary>是否默认开启点击穿透。</summary>
    public bool ClickThrough { get; init; }

    /// <summary>是否显示延迟与帧率叠加读数。</summary>
    public bool ShowLatencyOverlay { get; init; } = true;
}
