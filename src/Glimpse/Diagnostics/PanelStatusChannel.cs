namespace Glimpse.Diagnostics;

/// <summary>面板顶部横幅要表达的三类链路状态。</summary>
internal enum PanelStatusKind
{
    Ok,
    Switching,
    Error,
}

/// <summary>
/// 链路状态。由组合根在切换、热插拔、设备恢复时发布，面板按帧读取并绘制横幅；
/// 与 <see cref="LiveStatsChannel"/> 一样只做最新值传递，不排队。
/// </summary>
internal readonly record struct PanelStatus(
    PanelStatusKind Kind,
    string Headline,
    string? Detail,
    DateTime UpdatedAt)
{
    public static PanelStatus Ok(string headline) =>
        new(PanelStatusKind.Ok, headline, null, DateTime.Now);

    public static PanelStatus Switching(string headline) =>
        new(PanelStatusKind.Switching, headline, null, DateTime.Now);

    public static PanelStatus Error(string headline, string? detail) =>
        new(PanelStatusKind.Error, headline, detail, DateTime.Now);
}

internal sealed class PanelStatusChannel
{
    private readonly object _gate = new();
    private PanelStatus _latest = PanelStatus.Ok("就绪");

    public void Publish(PanelStatus status)
    {
        lock (_gate)
        {
            _latest = status;
        }
    }

    public PanelStatus Read()
    {
        lock (_gate)
        {
            return _latest;
        }
    }
}
