using Glimpse.Abstractions.Display;

namespace Glimpse.App;

/// <summary>最终选定的捕获源与预览屏。</summary>
internal readonly record struct MonitorSelection(MonitorDescriptor Source, MonitorDescriptor Preview);

/// <summary>
/// 启动参数。没有设置界面时，用 <c>--source</c> / <c>--preview</c> 指定显示器序号。
/// 例如在副屏看主屏：<c>Glimpse.exe --source 0 --preview 1</c>。
/// </summary>
internal sealed record StartupOptions(int? SourceMonitorIndex, int? PreviewMonitorIndex)
{
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        int? sourceIndex = null;
        int? previewIndex = null;

        for (int position = 0; position < args.Count; position++)
        {
            string argument = args[position];
            bool isSource = argument == "--source";
            bool isPreview = argument == "--preview";

            if ((!isSource && !isPreview) || position + 1 >= args.Count)
            {
                continue;
            }

            if (int.TryParse(args[position + 1], out int index))
            {
                if (isSource)
                {
                    sourceIndex = index;
                }
                else
                {
                    previewIndex = index;
                }
            }

            position++;
        }

        return new StartupOptions(sourceIndex, previewIndex);
    }

    /// <summary>
    /// 解析出实际屏幕。只指定一侧时，另一侧优先选择主屏或另一块屏。
    /// </summary>
    public MonitorSelection Resolve(IReadOnlyList<MonitorDescriptor> monitors, out string? warning)
    {
        if (monitors.Count == 0)
        {
            throw new ArgumentException("显示器列表不能为空。", nameof(monitors));
        }

        (MonitorDescriptor? Match, string? Warning) sourceRequest =
            FindRequested(SourceMonitorIndex, monitors, "source");
        (MonitorDescriptor? Match, string? Warning) previewRequest =
            FindRequested(PreviewMonitorIndex, monitors, "preview");

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

    private static (MonitorDescriptor? Match, string? Warning) FindRequested(
        int? index,
        IReadOnlyList<MonitorDescriptor> monitors,
        string name)
    {
        if (index is not { } value)
        {
            return (null, null);
        }

        MonitorDescriptor? match = monitors.FirstOrDefault(monitor => monitor.Index == value);
        if (match is not null)
        {
            return (match, null);
        }

        return (
            null,
            $"--{name} {value} 超出范围（有效值 0..{monitors.Count - 1}），已改用默认选择。");
    }
}
