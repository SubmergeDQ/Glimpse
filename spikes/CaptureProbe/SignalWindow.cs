using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace CaptureProbe;

/// <summary>
/// 信号源窗口：持续刷新高对比度画面，保证 WGC 有稳定帧可捕获。
/// 同屏验证时它同时充当“被捕获的副屏内容”。
/// </summary>
internal sealed class SignalWindow : Form
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private long _paintCount;

    public SignalWindow(Rectangle bounds)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        BackColor = Color.Black;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;

        _timer.Tick += (_, _) => Invalidate();
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _paintCount++;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 背景色随时间变化，确保每帧内容都有差异
        int phase = (int)(_clock.ElapsedMilliseconds % 1000);
        Color background = Color.FromArgb(
            20 + (phase * 200 / 1000),
            40,
            60 + (phase * 120 / 1000));
        g.Clear(background);

        // 水平移动的竖条，肉眼可直接观察延迟
        float x = (float)(_clock.ElapsedMilliseconds % 1000) / 1000f * ClientSize.Width;
        using (var brush = new SolidBrush(Color.FromArgb(255, 220, 60)))
        {
            g.FillRectangle(brush, x - 12, 0, 24, ClientSize.Height);
        }

        using var font = new Font("Consolas", Math.Max(32, ClientSize.Height / 8), FontStyle.Bold, GraphicsUnit.Pixel);
        string text = $"{_clock.ElapsedMilliseconds}";
        SizeF size = g.MeasureString(text, font);
        using (var brush = new SolidBrush(Color.White))
        {
            g.DrawString(text, font, brush, (ClientSize.Width - size.Width) / 2, (ClientSize.Height - size.Height) / 2);
        }

        using var small = new Font("Consolas", 22, FontStyle.Regular, GraphicsUnit.Pixel);
        g.DrawString($"paint={_paintCount}", small, Brushes.LightGray, 24, 24);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }
}
