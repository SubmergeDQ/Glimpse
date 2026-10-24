namespace Glimpse.Abstractions.Configuration;

/// <summary>
/// 预览窗的位置与尺寸，使用虚拟桌面坐标，允许跨屏摆放。
/// </summary>
public readonly record struct PreviewPlacement(int X, int Y, int Width, int Height);

/// <summary>预览窗在目标显示器内的贴边方式。</summary>
public enum PreviewAnchor
{
    TopRight,
    BottomRight,
    Center,
    Free,
}

/// <summary>
/// 应用设置。全部字段可空或带默认值，旧版本配置缺少字段时能直接反序列化。
/// </summary>
public sealed record AppSettings
{
    /// <summary>被预览的源显示器序号，空表示首次启动时自动选择。</summary>
    public int? SourceMonitorIndex { get; init; }

    /// <summary>预览窗所在的显示器序号。</summary>
    public int? PreviewMonitorIndex { get; init; }

    /// <summary>源显示器设备名（如 \\.\DISPLAY2），持久化主键。</summary>
    public string? SourceDeviceName { get; init; }

    /// <summary>预览显示器设备名，持久化主键。</summary>
    public string? PreviewDeviceName { get; init; }

    /// <summary>上次关闭时的窗口位置。</summary>
    public PreviewPlacement? Placement { get; init; }

    /// <summary>是否默认开启点击穿透。</summary>
    public bool ClickThrough { get; init; }

    /// <summary>是否显示延迟与帧率叠加读数。</summary>
    public bool ShowLatencyOverlay { get; init; } = true;

    /// <summary>预览窗尺寸档位，相对“适配目标屏的最大尺寸”的百分比。</summary>
    public int PreviewScalePercent { get; init; } = 50;

    /// <summary>预览窗贴边方式，自由表示保留用户拖动的位置。</summary>
    public PreviewAnchor PreviewAnchor { get; init; } = PreviewAnchor.BottomRight;

    /// <summary>面板基准测试的单次时长，单位秒。</summary>
    public int TestDurationSeconds { get; init; } = 60;

    /// <summary>是否让 WGC 把鼠标光标画进捕获帧。</summary>
    public bool CaptureCursor { get; init; } = true;

    /// <summary>是否把本程序窗口也拍进捕获结果（开启后预览窗以黑块出现在画面里）。</summary>
    public bool CaptureSelf { get; init; }

    /// <summary>预览窗是否始终位于其他窗口之上。</summary>
    public bool PreviewTopMost { get; init; } = true;

    /// <summary>是否已完成首次引导；为 false 时打开面板会显示三步向导。</summary>
    public bool Onboarded { get; init; }
}
