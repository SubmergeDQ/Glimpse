using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using Glimpse.Abstractions.Display;
using Glimpse.Diagnostics;
using Glimpse.Platform;

namespace Glimpse.Ui;

/// <summary>
/// 主控面板：无边框暗色窗口，自绘标题栏、图标轨与内容区。
/// 面板不参与交换链渲染，只按 1 Hz 读取 <see cref="LiveStatsChannel"/> 并重绘，
/// 隐藏到托盘时停表，不占用渲染或采集线程。
/// </summary>
internal sealed class MainPanelWindow : Form
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcHitTest = 0x0084;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int WmDpiChanged = 0x02E0;

    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    // 布局常量按 96 DPI 的逻辑像素书写，绘制前经 DpiScaling 换算
    private const int DesignWidth = 1440;
    private const int DesignHeight = 900;
    private const int MinLogicalWidth = 1180;
    private const int MinLogicalHeight = 720;
    private const int TitleBarHeight = 44;
    private const int RailWidth = 56;
    private const int StatusBarHeight = 26;
    private const int RightColumnWidth = 312;
    private const int WindowButtonWidth = 46;
    private const int ResizeBorder = 6;
    private const int WideThreshold = 1280;
    private const int BlockHeadHeight = 34;

    private enum PanelView
    {
        Monitor,
        Test,
        Diagnostics,
    }

    private enum PanelAction
    {
        None,
        Close,
        Minimize,
        Maximize,
        ViewMonitor,
        ViewTest,
        ViewDiagnostics,
        OpenLog,
        ToggleDrawer,
        RunBenchmark,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    private readonly LiveStatsChannel _stats;
    private readonly string _version;
    private readonly string _logFilePath;
    private readonly Func<MonitorDescriptor> _sourceMonitor;
    private readonly Func<MonitorDescriptor> _previewMonitor;
    private readonly Func<string> _adapterName;
    private readonly Func<bool> _captureExcluded;
    private readonly Func<(int Width, int Height)> _previewSize;
    private readonly Action _openLogDirectory;
    private readonly Action _runBenchmark;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly List<double> _p95History = new(60);

    private PanelView _view = PanelView.Monitor;
    private LiveStats _latest;
    private bool _live;
    private bool _drawerOpen;
    private bool _inSizeMove;
    private bool _initialized;
    private int _dpi;
    private PanelAction _hover = PanelAction.None;
    private PanelAction _pressed = PanelAction.None;

    public MainPanelWindow(
        string version,
        string logFilePath,
        LiveStatsChannel stats,
        Func<MonitorDescriptor> sourceMonitor,
        Func<MonitorDescriptor> previewMonitor,
        Func<string> adapterName,
        Func<bool> captureExcluded,
        Func<(int Width, int Height)> previewSize,
        Action openLogDirectory,
        Action runBenchmark)
    {
        _stats = stats;
        _version = version;
        _logFilePath = logFilePath;
        _sourceMonitor = sourceMonitor;
        _previewMonitor = previewMonitor;
        _adapterName = adapterName;
        _captureExcluded = captureExcluded;
        _previewSize = previewSize;
        _openLogDirectory = openLogDirectory;
        _runBenchmark = runBenchmark;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = PanelTheme.Background;
        Text = "Glimpse";
        MinimumSize = Size.Empty;

        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer,
            true);
        UpdateStyles();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshStats();
    }

    private int Dpi => _dpi > 0 ? _dpi : Math.Max(DpiScaling.DefaultDpi, DeviceDpi);

    private bool Wide => ClientSize.Width >= S(WideThreshold);

    private Rectangle BodyRect => new(
        0,
        S(TitleBarHeight),
        ClientSize.Width,
        Math.Max(0, ClientSize.Height - S(TitleBarHeight) - S(StatusBarHeight)));

    private Rectangle RailRect => new(0, BodyRect.Y, S(RailWidth), BodyRect.Height);

    private Rectangle AsideRect => new(
        ClientSize.Width - S(RightColumnWidth),
        BodyRect.Y,
        S(RightColumnWidth),
        BodyRect.Height);

    private Rectangle MainRect => new(
        S(RailWidth),
        BodyRect.Y,
        ClientSize.Width - S(RailWidth) - (Wide ? S(RightColumnWidth) : 0),
        BodyRect.Height);

    private Rectangle RunBarRect => new(
        MainRect.X + S(18),
        MainRect.Y + S(14),
        MainRect.Width - S(36),
        S(44));

    private Rectangle TopologyRect => new(
        MainRect.X + S(18),
        RunBarRect.Bottom + S(12),
        MainRect.Width - S(36),
        S(BlockHeadHeight + 194));

    private Rectangle BenchRect => new(
        MainRect.X + S(18),
        TopologyRect.Bottom + S(12),
        MainRect.Width - S(36),
        S(90));

    private Rectangle BenchRunButtonRect => new(
        BenchRect.Right - S(12) - S(156),
        BenchRect.Bottom - S(12) - S(28),
        S(156),
        S(28));

    private Rectangle DrawerButtonRect => new(
        RunBarRect.Right - S(64),
        RunBarRect.Y + (RunBarRect.Height - S(24)) / 2,
        S(64),
        S(24));

    private Rectangle DrawerCloseButtonRect => new(
        AsideRect.Right - S(14) - S(56),
        AsideRect.Y + S(14) + (S(BlockHeadHeight) - S(24)) / 2,
        S(56),
        S(24));

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _dpi = WindowPlacement.GetDpi(Handle);

        if (!_initialized)
        {
            _initialized = true;
            Size = new Size(S(DesignWidth), S(DesignHeight));
            CenterToScreen();
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);

        _timer.Enabled = Visible;
        if (Visible)
        {
            RefreshStats();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 关闭面板只收进托盘，捕获与呈现继续运行；真正退出走托盘菜单
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmGetMinMaxInfo)
        {
            base.WndProc(ref m);
            var info = Marshal.PtrToStructure<MinMaxInfo>(m.LParam);
            info.MinTrackSize.X = Math.Max(info.MinTrackSize.X, S(MinLogicalWidth));
            info.MinTrackSize.Y = Math.Max(info.MinTrackSize.Y, S(MinLogicalHeight));
            Marshal.StructureToPtr(info, m.LParam, false);
            return;
        }

        if (m.Msg == WmEnterSizeMove)
        {
            _inSizeMove = true;
            base.WndProc(ref m);
            return;
        }

        if (m.Msg == WmExitSizeMove)
        {
            _inSizeMove = false;
            base.WndProc(ref m);
            return;
        }

        if (m.Msg == WmDpiChanged)
        {
            HandleDpiChanged(m.WParam.ToInt32(), m.LParam);
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == WmNcHitTest)
        {
            m.Result = HitTestWindow(m.LParam);
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _pressed = HitAction(e.Location);
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        PanelAction action = HitAction(e.Location);
        if (action != PanelAction.None && action == _pressed)
        {
            Execute(action);
        }

        _pressed = PanelAction.None;
        base.OnMouseUp(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        PanelAction hover = HitAction(e.Location);
        if (hover != _hover)
        {
            _hover = hover;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != PanelAction.None)
        {
            _hover = PanelAction.None;
            Invalidate();
        }

        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.Clear(PanelTheme.Background);

        DrawTitleBar(graphics);
        DrawRail(graphics);
        DrawMainContent(graphics);

        if (Wide)
        {
            DrawAside(graphics, AsideRect, drawer: false);
        }
        else if (_drawerOpen)
        {
            DrawAside(graphics, AsideRect, drawer: true);
        }

        DrawStatusBar(graphics);
    }

    private void RefreshStats()
    {
        LiveStats stats = _stats.Read();
        _latest = stats;
        _live = stats.LastFrameAt is { } last && (DateTime.Now - last).TotalSeconds < 3;

        if (_live)
        {
            _p95History.Add(stats.PresentP95);
            if (_p95History.Count > 60)
            {
                _p95History.RemoveAt(0);
            }
        }

        Invalidate();
    }

    private void HandleDpiChanged(int wParam, IntPtr suggestedRectPointer)
    {
        int newDpi = wParam & 0xFFFF;
        if (newDpi > 0)
        {
            _dpi = newDpi;
        }

        if (!_inSizeMove || suggestedRectPointer == IntPtr.Zero)
        {
            Invalidate();
            return;
        }

        var rect = Marshal.PtrToStructure<NativeRect>(suggestedRectPointer);
        int width = Math.Max(rect.Right - rect.Left, S(MinLogicalWidth));
        int height = Math.Max(rect.Bottom - rect.Top, S(MinLogicalHeight));
        WindowPlacement.Place(Handle, rect.Left, rect.Top, width, height);
        Invalidate();
    }

    private IntPtr HitTestWindow(IntPtr screenPoint)
    {
        int screenX = unchecked((short)(long)screenPoint);
        int screenY = unchecked((short)((long)screenPoint >> 16));
        Point point = PointToClient(new Point(screenX, screenY));

        int border = S(ResizeBorder);
        bool left = point.X < border;
        bool right = point.X >= ClientSize.Width - border;
        bool top = point.Y < border;
        bool bottom = point.Y >= ClientSize.Height - border;

        if (top && left)
        {
            return new IntPtr(HtTopLeft);
        }

        if (top && right)
        {
            return new IntPtr(HtTopRight);
        }

        if (bottom && left)
        {
            return new IntPtr(HtBottomLeft);
        }

        if (bottom && right)
        {
            return new IntPtr(HtBottomRight);
        }

        if (left)
        {
            return new IntPtr(HtLeft);
        }

        if (right)
        {
            return new IntPtr(HtRight);
        }

        if (top)
        {
            return new IntPtr(HtTop);
        }

        if (bottom)
        {
            return new IntPtr(HtBottom);
        }

        if (point.Y < S(TitleBarHeight) && !IsWindowButton(point))
        {
            return new IntPtr(HtCaption);
        }

        return new IntPtr(HtClient);
    }

    private bool IsWindowButton(Point point) =>
        WindowButtonRect(0).Contains(point)
        || WindowButtonRect(1).Contains(point)
        || WindowButtonRect(2).Contains(point);

    private PanelAction HitAction(Point point)
    {
        for (int index = 0; index < 3; index++)
        {
            if (WindowButtonRect(index).Contains(point))
            {
                return index switch
                {
                    0 => PanelAction.Close,
                    1 => PanelAction.Maximize,
                    _ => PanelAction.Minimize,
                };
            }
        }

        for (int index = 0; index < 4; index++)
        {
            if (RailButtonRect(index).Contains(point))
            {
                return index switch
                {
                    0 => PanelAction.ViewMonitor,
                    1 => PanelAction.ViewTest,
                    2 => PanelAction.ViewDiagnostics,
                    _ => PanelAction.OpenLog,
                };
            }
        }

        if (_view == PanelView.Monitor && BenchRunButtonRect.Contains(point))
        {
            return PanelAction.RunBenchmark;
        }

        if (!Wide && DrawerButtonRect.Contains(point))
        {
            return PanelAction.ToggleDrawer;
        }

        if (!Wide && _drawerOpen && DrawerCloseButtonRect.Contains(point))
        {
            return PanelAction.ToggleDrawer;
        }

        return PanelAction.None;
    }

    private void Execute(PanelAction action)
    {
        switch (action)
        {
            case PanelAction.Close:
            case PanelAction.Minimize:
                Hide();
                break;

            case PanelAction.Maximize:
                WindowState = WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal
                    : FormWindowState.Maximized;
                break;

            case PanelAction.ViewMonitor:
                _view = PanelView.Monitor;
                _drawerOpen = false;
                break;

            case PanelAction.ViewTest:
                _view = PanelView.Test;
                _drawerOpen = false;
                break;

            case PanelAction.ViewDiagnostics:
                _view = PanelView.Diagnostics;
                _drawerOpen = false;
                break;

            case PanelAction.OpenLog:
                _openLogDirectory();
                break;

            case PanelAction.ToggleDrawer:
                _drawerOpen = !_drawerOpen;
                break;

            case PanelAction.RunBenchmark:
                _runBenchmark();
                break;
        }

        _hover = PanelAction.None;
        _pressed = PanelAction.None;
        Invalidate();
    }

    private void DrawTitleBar(Graphics graphics)
    {
        int height = S(TitleBarHeight);
        DrawLine(graphics, 0, height - 1, ClientSize.Width, height - 1, PanelTheme.Line);

        int mark = S(11);
        int markX = S(16);
        int markY = (height - mark) / 2;
        using (var pen = new Pen(PanelTheme.Amber, PenWidth(1)))
        {
            graphics.DrawRectangle(pen, markX, markY, mark - 1, mark - 1);
        }

        using (var brush = new SolidBrush(PanelTheme.Amber))
        {
            int inset = S(3);
            graphics.FillRectangle(brush, markX + inset, markY + inset, mark - (inset * 2), mark - (inset * 2));
        }

        int nameX = markX + mark + S(9);
        SizeF nameSize = Measure(graphics, "Glimpse", PanelTheme.Brand);
        DrawText(
            graphics,
            "Glimpse",
            PanelTheme.Brand,
            PanelTheme.Foreground,
            new Rectangle(nameX, 0, (int)Math.Ceiling(nameSize.Width) + S(2), height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        DrawText(
            graphics,
            _version,
            PanelTheme.MonoSmall,
            PanelTheme.Foreground3,
            new Rectangle(nameX + (int)Math.Ceiling(nameSize.Width) + S(8), 0, S(80), height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        DrawWindowButton(graphics, 0, PanelAction.Close);
        DrawWindowButton(graphics, 1, PanelAction.Maximize);
        DrawWindowButton(graphics, 2, PanelAction.Minimize);
    }

    private void DrawWindowButton(Graphics graphics, int indexFromRight, PanelAction action)
    {
        Rectangle rect = WindowButtonRect(indexFromRight);
        bool hover = _hover == action;

        if (hover)
        {
            Color fill = action == PanelAction.Close ? Color.FromArgb(196, 43, 28) : PanelTheme.Surface2;
            using var brush = new SolidBrush(fill);
            graphics.FillRectangle(brush, rect);
        }

        Color color = hover
            ? action == PanelAction.Close ? Color.White : PanelTheme.Foreground
            : PanelTheme.Foreground2;

        var pen = new Pen(color, PenWidth(1.1f));
        int centerX = rect.X + (rect.Width / 2);
        int centerY = rect.Y + (rect.Height / 2);
        int size = S(5);

        switch (action)
        {
            case PanelAction.Close:
                graphics.DrawLine(pen, centerX - size, centerY - size, centerX + size, centerY + size);
                graphics.DrawLine(pen, centerX + size, centerY - size, centerX - size, centerY + size);
                break;

            case PanelAction.Maximize:
                graphics.DrawRectangle(pen, centerX - size, centerY - size, size * 2, size * 2);
                break;

            default:
                graphics.DrawLine(pen, centerX - size, centerY, centerX + size, centerY);
                break;
        }

        pen.Dispose();
    }

    private void DrawRail(Graphics graphics)
    {
        Rectangle rail = RailRect;
        DrawLine(graphics, rail.Right - 1, rail.Y, rail.Right - 1, rail.Bottom, PanelTheme.Line);

        DrawRailButton(graphics, 0, PanelAction.ViewMonitor, "监看", PanelView.Monitor);
        DrawRailButton(graphics, 1, PanelAction.ViewTest, "测试", PanelView.Test);
        DrawRailButton(graphics, 2, PanelAction.ViewDiagnostics, "诊断", PanelView.Diagnostics);
        DrawRailButton(graphics, 3, PanelAction.OpenLog, "日志", null);
    }

    private void DrawRailButton(
        Graphics graphics,
        int index,
        PanelAction action,
        string label,
        PanelView? activeView)
    {
        Rectangle rect = RailButtonRect(index);
        bool active = activeView is { } view && _view == view;
        bool hover = _hover == action;

        if (active || hover)
        {
            using var brush = new SolidBrush(PanelTheme.Surface);
            graphics.FillRectangle(brush, rect);

            using var pen = new Pen(active ? PanelTheme.Line2 : PanelTheme.Line, PenWidth(1));
            graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }

        Color color = active
            ? PanelTheme.Amber
            : hover ? PanelTheme.Foreground2 : PanelTheme.Foreground3;

        int iconBox = S(20);
        var iconRect = new Rectangle(rect.X + (rect.Width - iconBox) / 2, rect.Y + S(8), iconBox, iconBox);

        switch (index)
        {
            case 0:
                DrawMonitorIcon(graphics, iconRect, color);
                break;
            case 1:
                DrawGaugeIcon(graphics, iconRect, color);
                break;
            case 2:
                DrawPulseIcon(graphics, iconRect, color);
                break;
            default:
                DrawDocumentIcon(graphics, iconRect, color);
                break;
        }

        DrawText(
            graphics,
            label,
            PanelTheme.Small,
            color,
            new Rectangle(rect.X, rect.Y + S(30), rect.Width, S(14)),
            StringAlignment.Center,
            StringAlignment.Center,
            ellipsis: false);
    }

    private void DrawMainContent(Graphics graphics)
    {
        Rectangle main = MainRect;
        if (main.Width <= 0 || main.Height <= 0)
        {
            return;
        }

        GraphicsState state = graphics.Save();
        graphics.SetClip(main);

        switch (_view)
        {
            case PanelView.Monitor:
                DrawRunBar(graphics);
                DrawTopology(graphics);
                DrawBenchSummary(graphics);
                DrawControlsPlaceholder(graphics);
                break;

            case PanelView.Test:
                DrawPlaceholder(
                    graphics,
                    "基准测试",
                    "下一小步接入：测试配置、运行进度与报告导出。");
                break;

            default:
                DrawPlaceholder(
                    graphics,
                    "诊断",
                    "下一小步接入：设备环境、日志尾与诊断摘要。");
                break;
        }

        graphics.Restore(state);
    }

    private void DrawRunBar(Graphics graphics)
    {
        Rectangle bar = RunBarRect;

        int dot = S(7);
        using (var brush = new SolidBrush(_live ? PanelTheme.Ok : PanelTheme.Error))
        {
            graphics.FillEllipse(brush, bar.X, bar.Y + ((bar.Height - dot) / 2), dot, dot);
        }

        int textX = bar.X + S(17);
        DrawText(
            graphics,
            _live ? "正在监看" : "源屏无画面",
            PanelTheme.BodyStrong,
            _live ? PanelTheme.Foreground : PanelTheme.Error,
            new Rectangle(textX, bar.Y, S(120), bar.Height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        MonitorDescriptor source = _sourceMonitor();
        MonitorDescriptor preview = _previewMonitor();
        string route =
            $"捕获 #{source.Index} {source.Width}×{source.Height} → 预览 #{preview.Index} {preview.Width}×{preview.Height}";

        int routeX = textX + S(130);
        int routeWidth = Math.Max(0, bar.Right - routeX - S(110) - (Wide ? 0 : S(74)));
        DrawText(
            graphics,
            route,
            PanelTheme.MonoSmall,
            PanelTheme.Foreground3,
            new Rectangle(routeX, bar.Y, routeWidth, bar.Height),
            StringAlignment.Near,
            StringAlignment.Center);

        int pillRight = bar.Right;
        if (!Wide)
        {
            DrawQuietButton(graphics, DrawerButtonRect, "读数", PanelAction.ToggleDrawer);
            pillRight = DrawerButtonRect.Left - S(10);
        }

        bool excluded = _captureExcluded();
        var pill = new Rectangle(pillRight - S(88), bar.Y + ((bar.Height - S(19)) / 2), S(88), S(19));
        DrawPill(graphics, pill, excluded ? "捕获隐身" : "未隐身", excluded ? PanelTheme.Ok : PanelTheme.Warn);

        DrawLine(graphics, bar.X, bar.Bottom, bar.Right - 1, bar.Bottom, PanelTheme.Line);
    }

    private void DrawTopology(Graphics graphics)
    {
        Rectangle block = TopologyRect;
        DrawBlock(graphics, block);
        DrawBlockHead(graphics, block, "投屏链路", "显存零拷贝");

        Rectangle body = new(block.X + 1, block.Y + S(BlockHeadHeight), block.Width - 2, block.Height - S(BlockHeadHeight) - 1);
        DrawGrid(graphics, body, S(28), Color.FromArgb(20, PanelTheme.Line));

        MonitorDescriptor source = _sourceMonitor();
        MonitorDescriptor preview = _previewMonitor();

        double sourceAspect = source.Width / (double)Math.Max(1, source.Height);
        double previewAspect = preview.Width / (double)Math.Max(1, preview.Height);

        int frameHeight = Math.Min(S(150), Math.Max(S(60), body.Height - S(72)));
        int linkGap = S(96);
        int sourceWidth = (int)Math.Round(frameHeight * sourceAspect);
        int previewWidth = (int)Math.Round(frameHeight * previewAspect);
        int total = sourceWidth + linkGap + previewWidth;
        int available = body.Width - S(40);

        if (total > available && total > 0)
        {
            double scale = available / (double)total;
            frameHeight = Math.Max(S(48), (int)Math.Round(frameHeight * scale));
            linkGap = Math.Max(S(52), (int)Math.Round(linkGap * scale));
            sourceWidth = Math.Max(S(28), (int)Math.Round(frameHeight * sourceAspect));
            previewWidth = Math.Max(S(28), (int)Math.Round(frameHeight * previewAspect));
            total = sourceWidth + linkGap + previewWidth;
        }

        int startX = body.X + ((body.Width - total) / 2);
        int frameY = body.Y + S(30);

        var sourceFrame = new Rectangle(startX, frameY, sourceWidth, frameHeight);
        var previewFrame = new Rectangle(startX + sourceWidth + linkGap, frameY, previewWidth, frameHeight);

        DrawMonitorFrame(
            graphics,
            sourceFrame,
            $"投屏源 · {(source.IsPrimary ? "主屏" : "副屏")}",
            source,
            _live ? PanelTheme.Amber : PanelTheme.Error);

        DrawMonitorFrame(
            graphics,
            previewFrame,
            $"预览输出 · {(preview.IsPrimary ? "主屏" : "副屏")}",
            preview,
            PanelTheme.Amber);

        DrawLink(graphics, sourceFrame, previewFrame, frameY + (frameHeight / 2));

        int footY = body.Bottom - S(24);
        string foot = $"捕获 WGC   /   合成 DXGI 翻转   /   帧池 2   /   丢帧 {_latest.Dropped}";
        DrawText(
            graphics,
            foot,
            PanelTheme.MonoSmall,
            PanelTheme.Foreground3,
            new Rectangle(body.X + S(12), footY, body.Width - S(230), S(18)),
            StringAlignment.Near,
            StringAlignment.Center);

        bool excluded = _captureExcluded();
        string pillText = excluded ? "预览窗不出现在捕获结果里" : "预览窗可能出现在捕获结果里";
        int pillWidth = S(excluded ? 190 : 190);
        var pill = new Rectangle(body.Right - S(12) - pillWidth, footY + ((S(18) - S(19)) / 2), pillWidth, S(19));
        DrawPill(graphics, pill, pillText, excluded ? PanelTheme.Ok : PanelTheme.Warn);
    }

    private void DrawMonitorFrame(
        Graphics graphics,
        Rectangle frame,
        string label,
        MonitorDescriptor monitor,
        Color borderColor)
    {
        DrawText(
            graphics,
            label,
            PanelTheme.Small,
            borderColor == PanelTheme.Amber ? PanelTheme.Amber : borderColor,
            new Rectangle(frame.X - S(40), frame.Y - S(20), frame.Width + S(80), S(16)),
            StringAlignment.Center,
            StringAlignment.Center,
            ellipsis: false);

        using (var hatch = new HatchBrush(
            HatchStyle.LightUpwardDiagonal,
            Color.FromArgb(255, 12, 14, 17),
            Color.FromArgb(255, 9, 11, 14)))
        {
            graphics.FillRectangle(hatch, frame);
        }

        using (var gridPen = new Pen(Color.FromArgb(18, 255, 255, 255), PenWidth(1)))
        {
            for (int x = frame.X + S(16); x < frame.Right; x += S(16))
            {
                graphics.DrawLine(gridPen, x, frame.Y + 1, x, frame.Bottom - 1);
            }

            for (int y = frame.Y + S(16); y < frame.Bottom; y += S(16))
            {
                graphics.DrawLine(gridPen, frame.X + 1, y, frame.Right - 1, y);
            }
        }

        using (var pen = new Pen(borderColor, PenWidth(1)))
        {
            graphics.DrawRectangle(pen, frame);
        }

        int dot = S(4);
        using (var brush = new SolidBrush(_live ? PanelTheme.Ok : PanelTheme.Error))
        {
            graphics.FillEllipse(brush, frame.X + S(7), frame.Y + S(7), dot, dot);
        }

        DrawText(
            graphics,
            _live ? "实时" : "无帧",
            PanelTheme.MonoSmall,
            _live ? PanelTheme.Ok : PanelTheme.Error,
            new Rectangle(frame.X + S(14), frame.Y + S(2), S(50), S(14)),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        DrawText(
            graphics,
            $"{monitor.Width}×{monitor.Height}",
            PanelTheme.MonoSmall,
            PanelTheme.Foreground3,
            new Rectangle(frame.X + S(6), frame.Bottom - S(16), frame.Width - S(12), S(14)),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        DrawText(
            graphics,
            $"{ShortDeviceName(monitor.DeviceName)} · {monitor.Width}×{monitor.Height}",
            PanelTheme.MonoSmall,
            PanelTheme.Foreground3,
            new Rectangle(frame.X - S(40), frame.Bottom + S(4), frame.Width + S(80), S(16)),
            StringAlignment.Center,
            StringAlignment.Center);
    }

    private void DrawLink(Graphics graphics, Rectangle sourceFrame, Rectangle previewFrame, int midY)
    {
        int startX = sourceFrame.Right + S(10);
        int endX = previewFrame.Left - S(10);
        Color color = _live ? PanelTheme.Line3 : PanelTheme.Error;

        using (var pen = new Pen(color, PenWidth(1)))
        {
            graphics.DrawLine(pen, startX, midY, endX, midY);

            int arrow = S(4);
            graphics.DrawLine(pen, endX - arrow, midY - arrow, endX, midY);
            graphics.DrawLine(pen, endX - arrow, midY + arrow, endX, midY);
        }

        if (_live)
        {
            int dots = S(3);
            using var brush = new SolidBrush(PanelTheme.Amber);
            for (int index = 1; index <= 3; index++)
            {
                int x = startX + (((endX - startX) * index) / 4);
                graphics.FillEllipse(brush, x - (dots / 2), midY - (dots / 2), dots, dots);
            }
        }

        DrawText(
            graphics,
            _live ? "WGC · 零拷贝" : "链路停止",
            PanelTheme.MonoSmall,
            _live ? PanelTheme.Foreground3 : PanelTheme.Error,
            new Rectangle(startX, midY - S(24), Math.Max(S(40), endX - startX), S(16)),
            StringAlignment.Center,
            StringAlignment.Center,
            ellipsis: false);
    }

    private void DrawBenchSummary(Graphics graphics)
    {
        Rectangle block = BenchRect;
        DrawBlock(graphics, block);
        DrawBlockHead(graphics, block, "基准测试", "配置在测试视图");

        DrawText(
            graphics,
            "尚未运行过基准测试",
            PanelTheme.MonoSmall,
            PanelTheme.Foreground3,
            new Rectangle(block.X + S(12), block.Y + S(BlockHeadHeight), block.Width - S(190), block.Height - S(BlockHeadHeight)),
            StringAlignment.Near,
            StringAlignment.Center);

        bool hover = _hover == PanelAction.RunBenchmark;
        Rectangle button = BenchRunButtonRect;
        using (var path = Rounded(button, S(2)))
        {
            using (var fill = new SolidBrush(hover ? Color.FromArgb(255, 188, 85) : PanelTheme.Amber))
            {
                graphics.FillPath(fill, path);
            }
        }

        DrawText(
            graphics,
            "运行 60 秒基准测试",
            PanelTheme.BodyStrong,
            PanelTheme.AmberInk,
            button,
            StringAlignment.Center,
            StringAlignment.Center,
            ellipsis: false);
    }

    private void DrawControlsPlaceholder(Graphics graphics)
    {
        int top = BenchRect.Bottom + S(12);
        int height = MainRect.Bottom - S(14) - top;
        if (height < S(80))
        {
            return;
        }

        var block = new Rectangle(MainRect.X + S(18), top, MainRect.Width - S(36), height);
        DrawBlock(graphics, block);
        DrawBlockHead(graphics, block, "选屏与输出控制", right: null);

        DrawText(
            graphics,
            "下一步接入：显示器卡片、预览输出控制与设置持久化。",
            PanelTheme.Body,
            PanelTheme.Foreground3,
            new Rectangle(block.X + S(12), block.Y + S(BlockHeadHeight), block.Width - S(24), block.Height - S(BlockHeadHeight)),
            StringAlignment.Center,
            StringAlignment.Center);
    }

    private void DrawPlaceholder(Graphics graphics, string title, string note)
    {
        var block = new Rectangle(MainRect.X + S(18), MainRect.Y + S(14), MainRect.Width - S(36), S(180));
        DrawBlock(graphics, block);
        DrawBlockHead(graphics, block, title, right: null);

        DrawText(
            graphics,
            note,
            PanelTheme.Body,
            PanelTheme.Foreground3,
            new Rectangle(block.X + S(12), block.Y + S(BlockHeadHeight), block.Width - S(24), block.Height - S(BlockHeadHeight)),
            StringAlignment.Near,
            StringAlignment.Center);
    }

    private void DrawAside(Graphics graphics, Rectangle aside, bool drawer)
    {
        if (aside.Width <= 0 || aside.Height <= 0)
        {
            return;
        }

        GraphicsState state = graphics.Save();
        graphics.SetClip(aside);

        using (var brush = new SolidBrush(PanelTheme.Background))
        {
            graphics.FillRectangle(brush, aside);
        }

        DrawLine(graphics, aside.X, aside.Y, aside.X, aside.Bottom, drawer ? PanelTheme.Line3 : PanelTheme.Line);

        var inner = new Rectangle(aside.X + S(14), aside.Y + S(14), aside.Width - S(28), aside.Height - S(28));
        int envHeight = S(BlockHeadHeight + (26 * 5) + 8);
        int cardHeight = inner.Height - envHeight - S(12);
        if (cardHeight < S(260))
        {
            cardHeight = Math.Max(S(220), inner.Height);
        }

        var card = new Rectangle(inner.X, inner.Y, inner.Width, cardHeight);
        DrawLiveCard(graphics, card, drawer);

        var env = new Rectangle(inner.X, card.Bottom + S(12), inner.Width, envHeight);
        if (env.Bottom <= aside.Bottom)
        {
            DrawEnvironment(graphics, env);
        }

        graphics.Restore(state);
    }

    private void DrawLiveCard(Graphics graphics, Rectangle card, bool drawer)
    {
        DrawBlock(graphics, card);

        int headHeight = S(BlockHeadHeight);
        int dot = S(7);
        using (var brush = new SolidBrush(_live ? PanelTheme.Ok : PanelTheme.Error))
        {
            graphics.FillEllipse(brush, card.X + S(12), card.Y + ((headHeight - dot) / 2), dot, dot);
        }

        DrawText(
            graphics,
            _live ? "实时画面" : "无画面",
            PanelTheme.BodyStrong,
            PanelTheme.Foreground2,
            new Rectangle(card.X + S(25), card.Y, S(100), headHeight),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        if (drawer)
        {
            DrawQuietButton(graphics, DrawerCloseButtonRect, "收起", PanelAction.ToggleDrawer);
        }
        else
        {
            (int width, int height) = _previewSize();
            DrawText(
                graphics,
                $"{width}×{height}",
                PanelTheme.MonoSmall,
                PanelTheme.Foreground3,
                new Rectangle(card.X, card.Y, card.Width - S(12), headHeight),
                StringAlignment.Far,
                StringAlignment.Center,
                ellipsis: false);
        }

        DrawLine(graphics, card.X + 1, card.Y + headHeight, card.Right - 1, card.Y + headHeight, PanelTheme.Line);

        Rectangle body = new(card.X + S(12), card.Y + headHeight + S(10), card.Width - S(24), card.Height - headHeight - S(20));
        if (body.Height <= 0)
        {
            return;
        }

        int sparkHeight = S(66);
        int noteHeight = S(34);
        int metersHeight = S((34 * 3) + 8);

        // 从上往下：缩略图、五项读数、公式注；剩余空间留给折线图上方
        int availableThumb = body.Height - sparkHeight - noteHeight - metersHeight - S(16);
        int thumbHeight = Math.Clamp(availableThumb, S(80), S(234));
        int metersY = body.Y + thumbHeight + S(8);
        int noteY = metersY + metersHeight;
        int sparkY = card.Bottom - S(12) - sparkHeight;
        if (sparkY < noteY + noteHeight)
        {
            return;
        }

        if (thumbHeight >= S(60))
        {
            DrawThumbnail(graphics, new Rectangle(body.X, body.Y, body.Width, thumbHeight));
        }

        DrawMeters(graphics, new Rectangle(body.X, metersY, body.Width, metersHeight));

        int noteLine = noteHeight / 2;
        DrawText(
            graphics,
            "回调年龄 = 采集 → 回调",
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(body.X, noteY, body.Width, noteLine),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        DrawText(
            graphics,
            "端到端 = 采集 → 回调 → 呈现",
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(body.X, noteY + noteLine, body.Width, noteLine),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        DrawSparkline(graphics, new Rectangle(body.X, sparkY, body.Width, sparkHeight));
    }

    private void DrawThumbnail(Graphics graphics, Rectangle area)
    {
        MonitorDescriptor source = _sourceMonitor();
        double aspect = source.Width / (double)Math.Max(1, source.Height);

        int height = Math.Min(area.Height - S(8), S(220));
        int width = (int)Math.Round(height * aspect);
        if (width > area.Width)
        {
            width = area.Width;
            height = (int)Math.Round(width / aspect);
        }

        if (height <= 0 || width <= 0)
        {
            return;
        }

        var thumb = new Rectangle(area.X + ((area.Width - width) / 2), area.Y + S(4), width, height);

        using (var hatch = new HatchBrush(
            HatchStyle.LightUpwardDiagonal,
            Color.FromArgb(255, 12, 14, 17),
            Color.FromArgb(255, 9, 11, 14)))
        {
            graphics.FillRectangle(hatch, thumb);
        }

        using (var pen = new Pen(_live ? PanelTheme.Line3 : Color.FromArgb(96, PanelTheme.Error), PenWidth(1)))
        {
            graphics.DrawRectangle(pen, thumb);
        }

        int dot = S(4);
        using (var brush = new SolidBrush(_live ? PanelTheme.Ok : PanelTheme.Error))
        {
            graphics.FillEllipse(brush, thumb.X + S(8), thumb.Y + S(7), dot, dot);
        }

        DrawText(
            graphics,
            _live ? "实时" : "无信号",
            PanelTheme.MonoSmall,
            _live ? PanelTheme.Ok : PanelTheme.Error,
            new Rectangle(thumb.X + S(15), thumb.Y + S(1), S(60), S(14)),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        DrawText(
            graphics,
            $"{source.Width}×{source.Height}",
            PanelTheme.MonoSmall,
            PanelTheme.Foreground3,
            new Rectangle(thumb.X, thumb.Bottom - S(18), thumb.Width - S(8), S(14)),
            StringAlignment.Far,
            StringAlignment.Center,
            ellipsis: false);
    }

    private void DrawMeters(Graphics graphics, Rectangle area)
    {
        int column = area.Width / 2;
        int rowHeight = S(34);
        Color valueColor = _live ? PanelTheme.Foreground : PanelTheme.Foreground3;

        DrawMeter(graphics, new Rectangle(area.X, area.Y, column, rowHeight), "捕获帧率", _live ? $"{_latest.CaptureFps:F1}" : "—", "fps", valueColor);
        DrawMeter(graphics, new Rectangle(area.X + column, area.Y, column, rowHeight), "呈现帧率", _live ? $"{_latest.ShowFps:F1}" : "—", "fps", valueColor);

        Color ageColor = _live && _latest.AgeP95 > 30 ? PanelTheme.Warn : valueColor;
        Color presentColor = _live && _latest.PresentP95 > 20 ? PanelTheme.Warn : valueColor;
        DrawMeter(graphics, new Rectangle(area.X, area.Y + rowHeight, column, rowHeight), "回调年龄 p95", _live ? $"{_latest.AgeP95:F1}" : "—", "ms", ageColor);
        DrawMeter(graphics, new Rectangle(area.X + column, area.Y + rowHeight, column, rowHeight), "端到端 p95", _live ? $"{_latest.PresentP95:F1}" : "—", "ms", presentColor);

        DrawMeter(graphics, new Rectangle(area.X, area.Y + (rowHeight * 2), column, rowHeight), "丢帧", _live ? _latest.Dropped.ToString(CultureInfo.InvariantCulture) : "—", null, valueColor);
    }

    private void DrawMeter(Graphics graphics, Rectangle rect, string label, string value, string? unit, Color valueColor)
    {
        DrawText(
            graphics,
            label,
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(rect.X, rect.Y, rect.Width, S(14)),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        var bounds = new Rectangle(rect.X, rect.Y + S(14), rect.Width, rect.Height - S(14));
        DrawText(
            graphics,
            value,
            PanelTheme.MonoValue,
            valueColor,
            bounds,
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        if (unit is null)
        {
            return;
        }

        SizeF valueSize = Measure(graphics, value, PanelTheme.MonoValue);
        DrawText(
            graphics,
            unit,
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(rect.X + (int)Math.Ceiling(valueSize.Width) + S(4), bounds.Y, rect.Width, bounds.Height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);
    }

    private void DrawSparkline(Graphics graphics, Rectangle area)
    {
        DrawLine(graphics, area.X, area.Y, area.Right, area.Y, PanelTheme.Line);

        var caption = new Rectangle(area.X, area.Y + S(7), area.Width, S(14));
        DrawText(
            graphics,
            "最近 60 秒延迟",
            PanelTheme.Small,
            PanelTheme.Foreground3,
            caption,
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        DrawText(
            graphics,
            "阈值 20 ms",
            PanelTheme.MonoSmall,
            PanelTheme.Foreground3,
            caption,
            StringAlignment.Far,
            StringAlignment.Center,
            ellipsis: false);

        var chart = new Rectangle(area.X, area.Y + S(24), area.Width, area.Height - S(28));
        if (chart.Height <= 0)
        {
            return;
        }

        const double maxMilliseconds = 40;
        int thresholdY = chart.Bottom - (int)Math.Round((20 / maxMilliseconds) * chart.Height);

        using (var pen = new Pen(PanelTheme.Line3, PenWidth(1)) { DashStyle = DashStyle.Dash })
        {
            graphics.DrawLine(pen, chart.X, thresholdY, chart.Right, thresholdY);
        }

        if (_p95History.Count < 2)
        {
            DrawLine(graphics, chart.X, chart.Bottom - 1, chart.Right, chart.Bottom - 1, PanelTheme.Line3);
            return;
        }

        var points = new PointF[_p95History.Count];
        for (int index = 0; index < _p95History.Count; index++)
        {
            double value = Math.Clamp(_p95History[index], 0, maxMilliseconds);
            points[index] = new PointF(
                chart.X + ((chart.Width - 1) * index / (float)(_p95History.Count - 1)),
                chart.Bottom - (float)((value / maxMilliseconds) * chart.Height));
        }

        using (var pen = new Pen(PanelTheme.Amber, PenWidth(1.2f)))
        {
            graphics.DrawLines(pen, points);
        }
    }

    private void DrawEnvironment(Graphics graphics, Rectangle block)
    {
        DrawBlock(graphics, block);
        DrawBlockHead(graphics, block, "设备与环境", right: null);

        int rowHeight = S(26);
        int y = block.Y + S(BlockHeadHeight) + S(4);
        string adapter = _adapterName();
        string scale = $"{Math.Round(Dpi * 100 / (double)DpiScaling.DefaultDpi)}%";

        DrawEnvironmentRow(graphics, new Rectangle(block.X + S(12), y, block.Width - S(24), rowHeight), "适配器", adapter, first: true);
        DrawEnvironmentRow(graphics, new Rectangle(block.X + S(12), y + rowHeight, block.Width - S(24), rowHeight), "合成路径", "DXGI 翻转 + DComp", first: false);
        DrawEnvironmentRow(graphics, new Rectangle(block.X + S(12), y + (rowHeight * 2), block.Width - S(24), rowHeight), "显存读回", "0 次", first: false);
        DrawEnvironmentRow(graphics, new Rectangle(block.X + S(12), y + (rowHeight * 3), block.Width - S(24), rowHeight), "跨屏缩放", scale, first: false);
        DrawEnvironmentRow(graphics, new Rectangle(block.X + S(12), y + (rowHeight * 4), block.Width - S(24), rowHeight), "单实例", "已持有", first: false, PanelTheme.Ok);
    }

    private void DrawEnvironmentRow(
        Graphics graphics,
        Rectangle rect,
        string label,
        string value,
        bool first,
        Color? valueColor = null)
    {
        if (!first)
        {
            DrawLine(graphics, rect.X, rect.Y, rect.Right, rect.Y, PanelTheme.Line);
        }

        DrawText(
            graphics,
            label,
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(rect.X, rect.Y, rect.Width / 2, rect.Height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        DrawText(
            graphics,
            value,
            PanelTheme.MonoSmall,
            valueColor ?? PanelTheme.Foreground2,
            new Rectangle(rect.X + (rect.Width / 2), rect.Y, rect.Width / 2, rect.Height),
            StringAlignment.Far,
            StringAlignment.Center);
    }

    private void DrawStatusBar(Graphics graphics)
    {
        int height = S(StatusBarHeight);
        int y = ClientSize.Height - height;
        DrawLine(graphics, 0, y, ClientSize.Width, y, PanelTheme.Line);

        DrawText(
            graphics,
            $"日志 {_logFilePath}",
            PanelTheme.MonoSmall,
            PanelTheme.Foreground3,
            new Rectangle(S(14), y, ClientSize.Width - S(320), height),
            StringAlignment.Near,
            StringAlignment.Center);

        DrawText(
            graphics,
            $"Ctrl+Alt+Q 退出    v{_version}",
            PanelTheme.MonoSmall,
            PanelTheme.Foreground3,
            new Rectangle(ClientSize.Width - S(14) - S(280), y, S(280), height),
            StringAlignment.Far,
            StringAlignment.Center,
            ellipsis: false);
    }

    private void DrawBlock(Graphics graphics, Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        using var path = Rounded(rect, S(2));
        using (var fill = new SolidBrush(PanelTheme.Surface))
        {
            graphics.FillPath(fill, path);
        }

        using (var pen = new Pen(PanelTheme.Line, PenWidth(1)))
        {
            graphics.DrawPath(pen, path);
        }
    }

    private void DrawBlockHead(Graphics graphics, Rectangle block, string title, string? right)
    {
        int height = S(BlockHeadHeight);

        DrawText(
            graphics,
            title,
            PanelTheme.BodyStrong,
            PanelTheme.Foreground2,
            new Rectangle(block.X + S(12), block.Y, block.Width - S(24), height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        if (right is not null)
        {
            DrawText(
                graphics,
                right,
                PanelTheme.MonoSmall,
                PanelTheme.Foreground3,
                new Rectangle(block.X, block.Y, block.Width - S(12), height),
                StringAlignment.Far,
                StringAlignment.Center);
        }

        DrawLine(graphics, block.X + 1, block.Y + height, block.Right - 1, block.Y + height, PanelTheme.Line);
    }

    private void DrawGrid(Graphics graphics, Rectangle rect, int step, Color color)
    {
        using var pen = new Pen(color, PenWidth(1));
        for (int x = rect.X + step; x < rect.Right; x += step)
        {
            graphics.DrawLine(pen, x, rect.Y, x, rect.Bottom);
        }

        for (int y = rect.Y + step; y < rect.Bottom; y += step)
        {
            graphics.DrawLine(pen, rect.X, y, rect.Right, y);
        }
    }

    private void DrawPill(Graphics graphics, Rectangle rect, string text, Color color)
    {
        using (var path = Rounded(rect, S(2)))
        {
            using var pen = new Pen(Color.FromArgb(96, color), PenWidth(1));
            graphics.DrawPath(pen, path);
        }

        int dot = S(5);
        using (var brush = new SolidBrush(color))
        {
            graphics.FillEllipse(brush, rect.X + S(7), rect.Y + ((rect.Height - dot) / 2), dot, dot);
        }

        DrawText(
            graphics,
            text,
            PanelTheme.Small,
            color,
            new Rectangle(rect.X + S(16), rect.Y, rect.Width - S(20), rect.Height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);
    }

    private void DrawQuietButton(Graphics graphics, Rectangle rect, string text, PanelAction action)
    {
        bool hover = _hover == action;
        if (hover)
        {
            using var brush = new SolidBrush(PanelTheme.Surface2);
            graphics.FillRectangle(brush, rect);
        }

        using (var pen = new Pen(PanelTheme.Line2, PenWidth(1)))
        {
            graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }

        DrawText(
            graphics,
            text,
            PanelTheme.Small,
            hover ? PanelTheme.Foreground : PanelTheme.Foreground3,
            rect,
            StringAlignment.Center,
            StringAlignment.Center,
            ellipsis: false);
    }

    private static void DrawText(
        Graphics graphics,
        string text,
        Font font,
        Color color,
        Rectangle bounds,
        StringAlignment align = StringAlignment.Near,
        StringAlignment lineAlign = StringAlignment.Center,
        bool ellipsis = true)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = align,
            LineAlignment = lineAlign,
            Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        graphics.DrawString(text, font, brush, bounds, format);
    }

    private static SizeF Measure(Graphics graphics, string text, Font font) =>
        graphics.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic);

    private void DrawLine(Graphics graphics, int x1, int y1, int x2, int y2, Color color)
    {
        using var pen = new Pen(color, PenWidth(1));
        graphics.DrawLine(pen, x1, y1, x2, y2);
    }

    private static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        int diameter = radius * 2;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private int S(int logicalPixels) => DpiScaling.Scale(logicalPixels, Dpi);

    private float PenWidth(float logicalPixels) =>
        Math.Max(1f, MathF.Round((float)(logicalPixels * Dpi / (double)DpiScaling.DefaultDpi)));

    private Rectangle WindowButtonRect(int indexFromRight)
    {
        int width = S(WindowButtonWidth);
        return new Rectangle(
            ClientSize.Width - (width * (indexFromRight + 1)),
            0,
            width,
            S(TitleBarHeight));
    }

    private Rectangle RailButtonRect(int index)
    {
        int width = S(40);
        int height = S(48);
        int x = (S(RailWidth) - width) / 2;
        int y = BodyRect.Y + S(10) + (index * (height + S(6)));
        return new Rectangle(x, y, width, height);
    }

    private static string ShortDeviceName(string deviceName)
    {
        int separator = deviceName.LastIndexOf('\\');
        return separator >= 0 && separator < deviceName.Length - 1
            ? deviceName[(separator + 1)..]
            : deviceName;
    }

    private void DrawMonitorIcon(Graphics graphics, Rectangle box, Color color)
    {
        var pen = new Pen(color, PenWidth(1.4f));
        int left = box.X + S(2);
        int top = box.Y + S(3);
        int width = box.Width - S(4);
        int height = box.Height - S(7);

        graphics.DrawRectangle(pen, left, top, width, height);
        graphics.DrawLine(pen, box.X + S(7), box.Bottom - S(2), box.Right - S(7), box.Bottom - S(2));
        graphics.DrawLine(pen, box.X + (box.Width / 2), top + height, box.X + (box.Width / 2), box.Bottom - S(2));
        pen.Dispose();
    }

    private void DrawGaugeIcon(Graphics graphics, Rectangle box, Color color)
    {
        var pen = new Pen(color, PenWidth(1.4f));
        var arc = new Rectangle(box.X + S(2), box.Y + S(4), box.Width - S(4), box.Height - S(6));
        graphics.DrawArc(pen, arc, 180, -180);
        graphics.DrawLine(pen, box.X + (box.Width / 2), box.Y + S(10), box.X + (box.Width / 2) + S(4), box.Y + S(5));

        int dot = S(3);
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, box.X + (box.Width / 2) - (dot / 2), box.Y + S(10) - (dot / 2), dot, dot);
        pen.Dispose();
    }

    private void DrawPulseIcon(Graphics graphics, Rectangle box, Color color)
    {
        var pen = new Pen(color, PenWidth(1.4f));
        Point[] points =
        [
            new(box.X + S(1), box.Y + S(10)),
            new(box.X + S(4), box.Y + S(10)),
            new(box.X + S(6), box.Y + S(5)),
            new(box.X + S(9), box.Y + S(14)),
            new(box.X + S(12), box.Y + S(7)),
            new(box.X + S(14), box.Y + S(10)),
            new(box.Right - S(1), box.Y + S(10)),
        ];
        graphics.DrawLines(pen, points);
        pen.Dispose();
    }

    private void DrawDocumentIcon(Graphics graphics, Rectangle box, Color color)
    {
        var pen = new Pen(color, PenWidth(1.4f));
        int left = box.X + S(4);
        int top = box.Y + S(2);
        int right = box.Right - S(4);
        int bottom = box.Bottom - S(2);

        graphics.DrawRectangle(pen, left, top, right - left, bottom - top);
        graphics.DrawLine(pen, left + S(2), top + S(6), right - S(2), top + S(6));
        graphics.DrawLine(pen, left + S(2), top + S(9), right - S(2), top + S(9));
        graphics.DrawLine(pen, left + S(2), top + S(12), left + S(6), top + S(12));
        pen.Dispose();
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
