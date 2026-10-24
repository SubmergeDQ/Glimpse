using Glimpse.Abstractions.Configuration;
using Glimpse.Abstractions.Display;
using Glimpse.Diagnostics;

namespace Glimpse.Ui;

/// <summary>诊断视图中的一行键值对。</summary>
internal sealed record DiagnosticEntry(string Label, string Value);

/// <summary>
/// 主控面板需要的全部数据源与动作。集中成一条记录，
/// 避免构造函数随功能增长到几十个位置参数而无法阅读。
/// </summary>
internal sealed record PanelBindings
{
    public required string Version { get; init; }

    public required string LogFilePath { get; init; }

    public required LiveStatsChannel Stats { get; init; }

    public required PanelStatusChannel Status { get; init; }

    public required Func<MonitorDescriptor> SourceMonitor { get; init; }

    public required Func<MonitorDescriptor> PreviewMonitor { get; init; }

    public required Func<IReadOnlyList<MonitorDescriptor>> Monitors { get; init; }

    public required Action<MonitorDescriptor> SelectSource { get; init; }

    public required Action<MonitorDescriptor> SelectPreview { get; init; }

    public required Action SwapDirection { get; init; }

    public required Action Reenumerate { get; init; }

    public required Func<bool> IsPaused { get; init; }

    public required Action TogglePause { get; init; }

    public required Func<bool> IsCursorCaptured { get; init; }

    public required Action<bool> SetCursorCaptured { get; init; }

    public required Func<bool> IsSelfCaptured { get; init; }

    public required Action<bool> SetSelfCaptured { get; init; }

    public required Func<bool> IsPreviewVisible { get; init; }

    public required Action TogglePreview { get; init; }

    public required Func<bool> IsOverlayVisible { get; init; }

    public required Action ToggleOverlay { get; init; }

    public required Func<bool> IsTopMost { get; init; }

    public required Action<bool> SetTopMost { get; init; }

    public required Func<int> PreviewScale { get; init; }

    public required Action<int> SetPreviewScale { get; init; }

    public required Func<PreviewAnchor> PreviewAnchor { get; init; }

    public required Action<PreviewAnchor> SetPreviewAnchor { get; init; }

    public required Action<int, Action<BenchmarkResult?, string?>> StartBenchmark { get; init; }

    public required Func<int> TestDuration { get; init; }

    public required Action<int> SetTestDuration { get; init; }

    public required Func<string> AdapterName { get; init; }

    public required Func<bool> CaptureExcluded { get; init; }

    public required Func<(int Width, int Height)> PreviewSize { get; init; }

    public required Action OpenLogDirectory { get; init; }

    public required Action RunBenchmark { get; init; }

    /// <summary>实际注册成功的退出组合键。注册回退到备选时状态栏提示要跟着变。</summary>
    public string[] ExitShortcutKeys { get; init; } = ["Ctrl", "Alt", "Q"];

    public string[] PauseShortcutKeys { get; init; } = ["Ctrl", "Alt", "P"];

    /// <summary>诊断视图的设备与管线快照，每次读取都应反映当前状态。</summary>
    public required Func<IReadOnlyList<DiagnosticEntry>> Diagnostics { get; init; }

    /// <summary>读取日志尾部若干行，供诊断视图滚动查看。</summary>
    public required Func<int, string[]> ReadLogTail { get; init; }

    /// <summary>把摘要与日志尾写入诊断包文件，返回文件路径。</summary>
    public required Func<string, string?> ExportDiagnostics { get; init; }

    /// <summary>设备创建是否走了回退路径，用于在面板提示适配器降级。</summary>
    public required Func<bool> AdapterFallback { get; init; }

    /// <summary>是否还未完成首次引导。</summary>
    public required Func<bool> ShouldOnboard { get; init; }

    /// <summary>引导完成或跳过后立即落盘，避免下次启动重复弹出。</summary>
    public required Action CompleteOnboarding { get; init; }

    /// <summary>提前结束当前基准测试，按已采集数据结算。</summary>
    public required Action StopBenchmark { get; init; }
}
