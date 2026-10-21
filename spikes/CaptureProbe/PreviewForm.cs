using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CaptureProbe;

/// <summary>
/// 置顶预览窗：无边框、可拖动、可切换点击穿透，并在画面上叠加端到端延迟读数。
/// </summary>
internal sealed class PreviewForm : Form
{
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    private readonly CaptureEngine _engine;
    private readonly System.Windows.Forms.Timer _renderTimer = new() { Interval = 16 };
    private Bitmap? _canvas;
    private byte[] _scaledBuffer = [];
    private bool _clickThrough;
    private bool _dragging;
    private Point _dragOffset;

    private double _lastCaptureSeconds;
    private double _measuredLatencyMs;
    private double _smoothedLatencyMs = double.NaN;
    private int _framesRendered;
    private int _offsetX;
    private int _offsetY;
    private readonly System.Diagnostics.Stopwatch _statsWindow = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<double> _windowLatencies = new();
    private int _fpsCounter;
    private double _fps = double.NaN;

    public PreviewForm(CaptureEngine engine, Rectangle bounds)
    {
        _engine = engine;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        BackColor = Color.Black;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        KeyPreview = true;

        _renderTimer.Tick += (_, _) => RenderLatestFrame();
        _renderTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_canvas is not null)
        {
            e.Graphics.DrawImageUnscaled(_canvas, _offsetX, _offsetY);
        }

        // 左上角叠加实时读数，方便截图取证
        string latency = double.IsNaN(_smoothedLatencyMs) ? "--" : _smoothedLatencyMs.ToString("F1");
        string fps = double.IsNaN(_fps) ? "--" : _fps.ToString("F0");
        string text = $"捕获→显示 {latency} ms   {fps} fps   {(_clickThrough ? "穿透中(按 T 关闭)" : "按 T 开启点击穿透")}";
        using var font = new Font("Consolas", 16, FontStyle.Bold, GraphicsUnit.Pixel);
        SizeF size = e.Graphics.MeasureString(text, font);
        using var background = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
        e.Graphics.FillRectangle(background, 8, 8, size.Width + 8, size.Height + 4);
        e.Graphics.DrawString(text, font, Brushes.Lime, 12, 10);
    }

    private void RenderLatestFrame()
    {
        FrameData? frame = _engine.TakeLatestFrame();
        if (frame is null)
        {
            return;
        }

        int width = ClientSize.Width;
        int height = ClientSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // 等比缩放并居中，避免竖屏/横屏混用时画面被拉伸
        double scale = Math.Min((double)width / frame.Width, (double)height / frame.Height);
        int contentWidth = Math.Max(1, (int)(frame.Width * scale));
        int contentHeight = Math.Max(1, (int)(frame.Height * scale));
        _offsetX = (width - contentWidth) / 2;
        _offsetY = (height - contentHeight) / 2;

        EnsureCanvas(contentWidth, contentHeight);
        double now = CaptureInterop.QueryPerfCounterSeconds();
        _lastCaptureSeconds = frame.CaptureSeconds;
        _measuredLatencyMs = (now - frame.CaptureSeconds) * 1000.0;
        _smoothedLatencyMs = double.IsNaN(_smoothedLatencyMs)
            ? _measuredLatencyMs
            : _smoothedLatencyMs * 0.85 + _measuredLatencyMs * 0.15;

        ScaleInto(frame, contentWidth, contentHeight);
        Invalidate();

        _framesRendered++;
        _fpsCounter++;
        _windowLatencies.Add(_measuredLatencyMs);

        if (_statsWindow.ElapsedMilliseconds >= 1000)
        {
            _fps = _fpsCounter * 1000.0 / _statsWindow.ElapsedMilliseconds;
            double[] sorted = _windowLatencies.OrderBy(v => v).ToArray();
            double average = sorted.Length > 0 ? sorted.Average() : 0;
            double p95 = sorted.Length > 0 ? sorted[(int)((sorted.Length - 1) * 0.95)] : 0;
            double max = sorted.Length > 0 ? sorted[^1] : 0;

            // 端到端 = 帧被系统捕获 → 预览窗完成绘制
            Console.WriteLine(
                $"显示统计  {_fps,4:F0} fps  端到端 {average,5:F1} / {p95,5:F1} / {max,5:F1} ms (avg/p95/max)");

            _statsWindow.Restart();
            _windowLatencies.Clear();
            _fpsCounter = 0;
        }
    }

    private void EnsureCanvas(int width, int height)
    {
        if (_canvas is not null && _canvas.Width == width && _canvas.Height == height)
        {
            return;
        }

        _canvas?.Dispose();
        _canvas = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        _scaledBuffer = new byte[width * height * 4];
    }

    /// <summary>
    /// 最近邻缩放到窗口尺寸。验证阶段刻意避开 GDI+ 的整幅缩放，减少无关开销。
    /// </summary>
    private void ScaleInto(FrameData frame, int dstWidth, int dstHeight)
    {
        byte[] source = frame.Pixels;
        byte[] target = _scaledBuffer;
        int srcWidth = frame.Width;
        int srcHeight = frame.Height;

        for (int y = 0; y < dstHeight; y++)
        {
            int srcY = y * srcHeight / dstHeight;
            int srcRow = srcY * srcWidth * 4;
            int dstRow = y * dstWidth * 4;

            for (int x = 0; x < dstWidth; x++)
            {
                int srcIndex = srcRow + x * srcWidth / dstWidth * 4;
                int dstIndex = dstRow + x * 4;
                target[dstIndex] = source[srcIndex];
                target[dstIndex + 1] = source[srcIndex + 1];
                target[dstIndex + 2] = source[srcIndex + 2];
                target[dstIndex + 3] = source[srcIndex + 3];
            }
        }

        BitmapData data = _canvas!.LockBits(
            new Rectangle(0, 0, dstWidth, dstHeight),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            for (int y = 0; y < dstHeight; y++)
            {
                Marshal.Copy(target, y * dstWidth * 4, data.Scan0 + y * data.Stride, dstWidth * 4);
            }
        }
        finally
        {
            _canvas.UnlockBits(data);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Application.Exit();
            return;
        }

        if (e.KeyCode == Keys.T)
        {
            _clickThrough = !_clickThrough;
            ApplyClickThrough(_clickThrough);
            Invalidate();
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && !_clickThrough)
        {
            _dragging = true;
            _dragOffset = e.Location;
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            Location = new Point(Left + e.X - _dragOffset.X, Top + e.Y - _dragOffset.Y);
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        base.OnMouseUp(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest && _clickThrough)
        {
            m.Result = HtTransparent;
            return;
        }

        base.WndProc(ref m);
    }

    private void ApplyClickThrough(bool enabled)
    {
        // 置顶窗穿透后不应激活，否则会抢走舞台控制软件的前台焦点
        int styles = GetWindowLong(Handle, -20);
        int updated = enabled ? styles | 0x20 | 0x8000000 : styles & ~(0x20 | 0x8000000);
        SetWindowLong(Handle, -20, updated);
    }

    /// <summary>
    /// 用 WindowFromPoint 验证穿透是否真的让窗口“不可命中”，无需真实点击。
    /// </summary>
    public void RunClickThroughSelfTest()
    {
        var center = new Point(Left + Width / 2, Top + Height / 2);

        _clickThrough = false;
        ApplyClickThrough(false);
        IntPtr hitWithout = WindowFromPoint(center);

        _clickThrough = true;
        ApplyClickThrough(true);
        IntPtr hitWith = WindowFromPoint(center);

        Console.WriteLine(
            $"[穿透自检] 普通状态命中自身: {hitWithout == Handle}; " +
            $"穿透状态命中自身: {hitWith == Handle} (命中窗口 {hitWith})");

        _clickThrough = false;
        ApplyClickThrough(false);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderTimer.Dispose();
            _canvas?.Dispose();
        }

        base.Dispose(disposing);
    }
}
