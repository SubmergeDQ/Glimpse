using System.Drawing.Text;
using Glimpse.Diagnostics;
using Glimpse.Platform;

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

    private const int WmDpiChanged = 0x02E0;

    // 界面常量按 96 DPI 的逻辑像素书写，使用前经 DpiScaling 换算
    private const int LogicalWidth = 154;
    private const int LogicalHeight = 48;
    private const int LogicalFontPixels = 13;
    private const int LogicalPadding = 8;
    private const int LogicalValueLeft = 52;
    private const int LogicalFirstRow = 5;
    private const int LogicalSecondRow = 25;
    private const int LogicalStuckRow = 15;

    private static readonly Color NormalColor = Color.FromArgb(69, 214, 160);
    private static readonly Color WarnColor = Color.FromArgb(242, 169, 59);
    private static readonly Color ErrorColor = Color.FromArgb(239, 91, 91);
    private static readonly Color LabelColor = Color.FromArgb(138, 144, 153);

    private readonly LiveStatsChannel _channel;
    private readonly System.Windows.Forms.Timer _timer;

    private Font _font = CreateFont(DpiScaling.DefaultDpi);
    private LiveStats _stats;
    private int _dpi = DpiScaling.DefaultDpi;

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
        Size = new Size(
            DpiScaling.Scale(LogicalWidth, _dpi),
            DpiScaling.Scale(LogicalHeight, _dpi));

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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _dpi = WindowPlacement.GetDpi(Handle);
        ApplyDpiLayout();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmDpiChanged)
        {
            // 读数框尺寸由固定逻辑尺寸决定，不跟随建议矩形（避免在不同屏之间越拖越偏）
            int newDpi = m.WParam.ToInt32() & 0xFFFF;
            if (newDpi > 0)
            {
                _dpi = newDpi;
                ApplyDpiLayout();
            }

            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
    }

    private static Font CreateFont(int dpi) =>
        new("Consolas", DpiScaling.Scale(LogicalFontPixels, dpi), FontStyle.Bold, GraphicsUnit.Pixel);

    private void ApplyDpiLayout()
    {
        Font previous = _font;
        _font = CreateFont(_dpi);
        previous.Dispose();

        Size = new Size(
            DpiScaling.Scale(LogicalWidth, _dpi),
            DpiScaling.Scale(LogicalHeight, _dpi));
        Invalidate();
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

        int padding = DpiScaling.Scale(LogicalPadding, _dpi);
        int valueLeft = DpiScaling.Scale(LogicalValueLeft, _dpi);
        int firstRow = DpiScaling.Scale(LogicalFirstRow, _dpi);
        int secondRow = DpiScaling.Scale(LogicalSecondRow, _dpi);
        int stuckRow = DpiScaling.Scale(LogicalStuckRow, _dpi);

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

            e.Graphics.DrawString(stuck, _font, valueBrush, padding, stuckRow);
            return;
        }

        e.Graphics.DrawString("show", _font, labelBrush, padding, firstRow);
        e.Graphics.DrawString($"{_stats.ShowFps,3:0} fps", _font, valueBrush, valueLeft, firstRow);

        e.Graphics.DrawString("p95", _font, labelBrush, padding, secondRow);
        e.Graphics.DrawString($"{_stats.PresentP95,4:0.0} ms", _font, valueBrush, valueLeft, secondRow);
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
