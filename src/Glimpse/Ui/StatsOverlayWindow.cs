using System.Drawing.Text;
using Glimpse.Diagnostics;

namespace Glimpse.Ui;

/// <summary>
/// 预览窗内的实时读数。独立顶层窗口，不进入交换链渲染路径；
/// 自身鼠标穿透，不干扰预览窗的拖动与缩放。
/// </summary>
internal sealed class StatsOverlayWindow : Form
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExTransparent = 0x00000020;

    private static readonly Color NormalColor = Color.FromArgb(69, 214, 160);
    private static readonly Color WarnColor = Color.FromArgb(242, 169, 59);
    private static readonly Color ErrorColor = Color.FromArgb(239, 91, 91);
    private static readonly Color LabelColor = Color.FromArgb(138, 144, 153);

    private readonly LiveStatsChannel _channel;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Font _font = new("Consolas", 13, FontStyle.Bold, GraphicsUnit.Pixel);

    private LiveStats _stats;

    public StatsOverlayWindow(LiveStatsChannel channel)
    {
        _channel = channel;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(8, 9, 11);
        Opacity = 0.82;
        Size = new Size(154, 48);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshStats();
        _timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.Style = WsPopup;
            parameters.ExStyle |= unchecked((int)(WsExToolWindow | WsExNoActivate | WsExTransparent));
            return parameters;
        }
    }

    public void RefreshNow() => RefreshStats();

    private void RefreshStats()
    {
        _stats = _channel.Read();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        bool noFrames = _stats.LastFrameAt is null
            || (DateTime.Now - _stats.LastFrameAt.Value).TotalSeconds >= 3;

        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using var labelBrush = new SolidBrush(LabelColor);
        using var valueBrush = new SolidBrush(
            noFrames
                ? ErrorColor
                : _stats.PresentP95 > 20
                    ? WarnColor
                    : NormalColor);

        if (noFrames)
        {
            string stuck = _stats.LastFrameAt is { } last
                ? $"no frame {(DateTime.Now - last).TotalSeconds:0}s"
                : "no frame";

            e.Graphics.DrawString(stuck, _font, valueBrush, 8, 15);
            return;
        }

        e.Graphics.DrawString("show", _font, labelBrush, 8, 5);
        e.Graphics.DrawString($"{_stats.ShowFps,3:0} fps", _font, valueBrush, 52, 5);

        e.Graphics.DrawString("p95", _font, labelBrush, 8, 25);
        e.Graphics.DrawString($"{_stats.PresentP95,4:0.0} ms", _font, valueBrush, 52, 25);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _font.Dispose();
        }

        base.Dispose(disposing);
    }
}
