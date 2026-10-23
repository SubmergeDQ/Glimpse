using Glimpse.Abstractions.Configuration;
using Glimpse.Abstractions.Display;

namespace Glimpse.App;

/// <summary>最终选定的捕获源与预览屏。</summary>
internal readonly record struct MonitorSelection(MonitorDescriptor Source, MonitorDescriptor Preview);

/// <summary>
/// 启动参数。没有设置界面时，用 <c>--source</c> / <c>--preview</c> 指定显示器序号，
/// <c>--benchmark 60</c> 表示启动后自动跑指定秒数的基准测试并退出。
/// 例如在副屏看主屏：<c>Glimpse.exe --source 0 --preview 1</c>。
/// </summary>
internal sealed record StartupOptions(
    int? SourceMonitorIndex,
    int? PreviewMonitorIndex,
    int? BenchmarkSeconds = null)
{
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        int? sourceIndex = null;
        int? previewIndex = null;
        int? benchmarkSeconds = null;

        for (int position = 0; position < args.Count; position++)
        {
            string argument = args[position];
            bool isSource = argument == "--source";
            bool isPreview = argument == "--preview";
            bool isBenchmark = argument == "--benchmark";

            if ((!isSource && !isPreview && !isBenchmark) || position + 1 >= args.Count)
            {
                continue;
            }

            if (int.TryParse(args[position + 1], out int value))
            {
                if (isSource)
                {
                    sourceIndex = value;
                }
                else if (isPreview)
                {
                    previewIndex = value;
                }
                else
                {
                    benchmarkSeconds = value;
                }
            }

            position++;
        }

        return new StartupOptions(sourceIndex, previewIndex, benchmarkSeconds);
    }

    /// <summary>
    /// 解析出实际屏幕。命令行参数优先于设置；设置内部设备名优先于序号。
    /// 只指定一侧时，另一侧优先选择主屏或另一块屏。
    /// </summary>
    public MonitorSelection Resolve(
        IReadOnlyList<MonitorDescriptor> monitors,
        AppSettings settings,
        out string? warning)
    {
        if (monitors.Count == 0)
        {
            throw new ArgumentException("显示器列表不能为空。", nameof(monitors));
        }

        (MonitorDescriptor? Match, string? Warning) sourceRequest = SourceMonitorIndex is { } sourceIndex
            ? FindRequested(sourceIndex, null, monitors, "--source")
            : FindRequested(settings.SourceMonitorIndex, settings.SourceDeviceName, monitors, "设置里的源屏");

        (MonitorDescriptor? Match, string? Warning) previewRequest = PreviewMonitorIndex is { } previewIndex
            ? FindRequested(previewIndex, null, monitors, "--preview")
            : FindRequested(settings.PreviewMonitorIndex, settings.PreviewDeviceName, monitors, "设置里的预览屏");

        warning = sourceRequest.Warning ?? previewRequest.Warning;

        if (sourceRequest.Match is { } requestedSource && previewRequest.Match is { } requestedPreview)
        {
            return new MonitorSelection(requestedSource, requestedPreview);
        }

        if (sourceRequest.Match is { } onlySource)
        {
            MonitorDescriptor preview = monitors.FirstOrDefault(
                    monitor => monitor.Index != onlySource.Index && monitor.IsPrimary)
                ?? monitors.FirstOrDefault(monitor => monitor.Index != onlySource.Index)
                ?? onlySource;
            return new MonitorSelection(onlySource, preview);
        }

        if (previewRequest.Match is { } onlyPreview)
        {
            MonitorDescriptor source = monitors.FirstOrDefault(
                    monitor => monitor.IsPrimary && monitor.Index != onlyPreview.Index)
                ?? monitors.FirstOrDefault(monitor => monitor.Index != onlyPreview.Index)
                ?? onlyPreview;
            return new MonitorSelection(source, onlyPreview);
        }

        MonitorDescriptor defaultPreview = monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
        MonitorDescriptor defaultSource = monitors.FirstOrDefault(monitor => !monitor.IsPrimary) ?? defaultPreview;
        return new MonitorSelection(defaultSource, defaultPreview);
    }

    /// <summary>没有设置时的入口，保持纯命令行行为。</summary>
    public MonitorSelection Resolve(IReadOnlyList<MonitorDescriptor> monitors, out string? warning) =>
        Resolve(monitors, new AppSettings(), out warning);

    private static (MonitorDescriptor? Match, string? Warning) FindRequested(
        int? index,
        string? deviceName,
        IReadOnlyList<MonitorDescriptor> monitors,
        string label)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            MonitorDescriptor? byDevice = monitors.FirstOrDefault(
                monitor => string.Equals(monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (byDevice is not null)
            {
                return (byDevice, null);
            }
        }

        if (index is { } value)
        {
            MonitorDescriptor? match = monitors.FirstOrDefault(monitor => monitor.Index == value);
            if (match is not null)
            {
                return (match, null);
            }

            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                return (null, $"{label} 的设备 {deviceName} 与序号 {value} 均无效，已改用默认选择。");
            }

            return (null, $"{label} {value} 超出范围（有效值 0..{monitors.Count - 1}），已改用默认选择。");
        }

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            return (null, $"{label} 的设备 {deviceName} 未找到，已改用默认选择。");
        }

        return (null, null);
    }
}
