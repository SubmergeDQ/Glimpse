using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Glimpse.Abstractions.Configuration;
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
    private const int CollapsedWidth = 520;
    private const int CollapsedHeight = 52;
    private const int CollapsedBottomMargin = 12;

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
        SwapDirection,
        Reenumerate,
        TogglePause,
        ToggleCursorCapture,
        ToggleSelfCapture,
        TogglePreview,
        ToggleOverlay,
        ToggleTopMost,
        Scale25,
        Scale50,
        Scale75,
        Scale100,
        AnchorTopRight,
        AnchorBottomRight,
        AnchorCenter,
        AnchorFree,
        StartBenchmark,
        Duration15,
        Duration60,
        Duration300,
        CopyDiagnostics,
        ExportDiagnostics,
        Collapse,
        Expand,
        OnboardSkip,
        OnboardPrev,
        OnboardNext,
        OnboardStart,
        OnboardFinish,
        StopBenchmark,
    }

    private readonly record struct CardKey(int Index, bool Source)
    {
        public static CardKey None => new(-1, false);

        public bool IsNone => Index < 0;
    }

    private readonly record struct CardHit(CardKey Key, MonitorDescriptor Monitor);

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
    private readonly PanelStatusChannel _statusChannel;
    private readonly string _version;
    private readonly string _logFilePath;
    private readonly Func<MonitorDescriptor> _sourceMonitor;
    private readonly Func<MonitorDescriptor> _previewMonitor;
    private readonly Func<IReadOnlyList<MonitorDescriptor>> _monitors;
    private readonly Action<MonitorDescriptor> _selectSource;
    private readonly Action<MonitorDescriptor> _selectPreview;
    private readonly Action _swapDirection;
    private readonly Action _reenumerate;
    private readonly Func<bool> _isPaused;
    private readonly Action _togglePause;
    private readonly Func<bool> _isCursorCaptured;
    private readonly Action<bool> _setCursorCaptured;
    private readonly Func<bool> _isSelfCaptured;
    private readonly Action<bool> _setSelfCaptured;
    private readonly Func<bool> _isPreviewVisible;
    private readonly Action _togglePreview;
    private readonly Func<bool> _isOverlayVisible;
    private readonly Action _toggleOverlay;
    private readonly Func<bool> _isTopMost;
    private readonly Action<bool> _setTopMost;
    private readonly Func<int> _previewScale;
    private readonly Action<int> _setPreviewScale;
    private readonly Func<PreviewAnchor> _previewAnchor;
    private readonly Action<PreviewAnchor> _setPreviewAnchor;
    private readonly Action<int, Action<BenchmarkResult?, string?>> _startBenchmark;
    private readonly Func<int> _testDuration;
    private readonly Action<int> _setTestDuration;
    private readonly System.Windows.Forms.Timer _benchmarkTimer;
    private readonly Func<string> _adapterName;
    private readonly Func<bool> _captureExcluded;
    private readonly Func<(int Width, int Height)> _previewSize;
    private readonly Action _openLogDirectory;
    private readonly Action _runBenchmark;
    private readonly string[] _exitShortcutKeys;
    private readonly string[] _pauseShortcutKeys;
    private readonly Func<IReadOnlyList<DiagnosticEntry>> _diagnostics;
    private readonly Func<int, string[]> _readLogTail;
    private readonly Func<string, string?> _exportDiagnostics;
    private readonly Func<bool> _adapterFallback;
    private readonly Func<bool> _shouldOnboard;
    private readonly Action _completeOnboarding;
    private readonly Action _stopBenchmark;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer _animationTimer;
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
    private CardKey _hoverCard = CardKey.None;
    private CardKey _pressedCard = CardKey.None;
    private const int StatusMinVisibleMs = 1600;

    private PanelStatus _status = PanelStatus.Ok("就绪");
    private DateTime _statusShownAt = DateTime.Now;
    private bool _switching;
    private BenchmarkResult? _lastBenchmark;
    private string? _lastReportPath;
    private bool _benchmarkRunning;
    private DateTime _benchmarkStartedAt;
    private int _benchmarkRunSeconds = 60;
    private IReadOnlyList<DiagnosticEntry> _diagnosticsSnapshot = [];
    private string[] _logLines = [];
    private int _logScroll;
    private long _logLoadedAt;
    private string? _diagnosticsNotice;
    private DateTime _diagnosticsNoticeAt;
    private bool _collapsed;
    private (int X, int Y, int Width, int Height) _expandedBounds;
    private bool _onboardingChecked;
    private int _onboardingStep = -1;
    private bool _onboardRunStarted;
    private double _linkPhase;

    public MainPanelWindow(PanelBindings bindings)
    {
        _stats = bindings.Stats;
        _statusChannel = bindings.Status;
        _version = bindings.Version;
        _logFilePath = bindings.LogFilePath;
        _sourceMonitor = bindings.SourceMonitor;
        _previewMonitor = bindings.PreviewMonitor;
        _monitors = bindings.Monitors;
        _selectSource = bindings.SelectSource;
        _selectPreview = bindings.SelectPreview;
        _swapDirection = bindings.SwapDirection;
        _reenumerate = bindings.Reenumerate;
        _isPaused = bindings.IsPaused;
        _togglePause = bindings.TogglePause;
        _isCursorCaptured = bindings.IsCursorCaptured;
        _setCursorCaptured = bindings.SetCursorCaptured;
        _isSelfCaptured = bindings.IsSelfCaptured;
        _setSelfCaptured = bindings.SetSelfCaptured;
        _isPreviewVisible = bindings.IsPreviewVisible;
        _togglePreview = bindings.TogglePreview;
        _isOverlayVisible = bindings.IsOverlayVisible;
        _toggleOverlay = bindings.ToggleOverlay;
        _isTopMost = bindings.IsTopMost;
        _setTopMost = bindings.SetTopMost;
        _previewScale = bindings.PreviewScale;
        _setPreviewScale = bindings.SetPreviewScale;
        _previewAnchor = bindings.PreviewAnchor;
        _setPreviewAnchor = bindings.SetPreviewAnchor;
        _startBenchmark = bindings.StartBenchmark;
        _testDuration = bindings.TestDuration;
        _setTestDuration = bindings.SetTestDuration;
        _adapterName = bindings.AdapterName;
        _captureExcluded = bindings.CaptureExcluded;
        _previewSize = bindings.PreviewSize;
        _openLogDirectory = bindings.OpenLogDirectory;
        _runBenchmark = bindings.RunBenchmark;
        _exitShortcutKeys = bindings.ExitShortcutKeys;
        _pauseShortcutKeys = bindings.PauseShortcutKeys;
        _diagnostics = bindings.Diagnostics;
        _readLogTail = bindings.ReadLogTail;
        _exportDiagnostics = bindings.ExportDiagnostics;
        _adapterFallback = bindings.AdapterFallback;
        _shouldOnboard = bindings.ShouldOnboard;
        _completeOnboarding = bindings.CompleteOnboarding;
        _stopBenchmark = bindings.StopBenchmark;

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

        // 链路状态单独快轮询：设备恢复等过渡可能远短于 1 秒的读数节拍
        _statusTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _statusTimer.Tick += (_, _) => SyncStatus();

        _benchmarkTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _benchmarkTimer.Tick += (_, _) => Invalidate();

        // 链路上的黄点单独走 60ms 节拍，比读数刷新更顺滑；不可见视图里直接跳过重绘
        _animationTimer = new System.Windows.Forms.Timer { Interval = 60 };
        _animationTimer.Tick += (_, _) => AdvanceLinkAnimation();
    }

    /// <summary>供组合根在切换输出参数后立即刷新读数与选中态。</summary>
    public void RefreshNow() => RefreshStats();

    /// <summary>
    /// 显示之后再切换 TopMost：WinForms 构造期设置的 TopMost 不会落到 WS_EX_TOPMOST。
    /// 面板与预览窗都置顶时，谁最后被激活谁在上层。
    /// </summary>
    public void SetTopMostEnabled(bool enabled)
    {
        TopMost = false;
        if (enabled)
        {
            TopMost = true;
        }
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

    private bool ShowBanner =>
        _status.Kind is PanelStatusKind.Switching or PanelStatusKind.Error;

    private Rectangle BannerRect => ShowBanner
        ? new Rectangle(MainRect.X + S(18), RunBarRect.Bottom + S(8), MainRect.Width - S(36), S(36))
        : Rectangle.Empty;

    private int ContentTop => (ShowBanner ? BannerRect.Bottom : RunBarRect.Bottom) + S(12);

    private Rectangle TopologyRect => new(
        MainRect.X + S(18),
        ContentTop,
        MainRect.Width - S(36),
        S(BlockHeadHeight + 194));

    private int ControlBlockHeight => Math.Min(
        S(340),
        Math.Max(S(292), MainRect.Bottom - S(14) - (TopologyRect.Bottom + S(12))));

    private Rectangle SourceBlockRect => new(
        MainRect.X + S(18),
        TopologyRect.Bottom + S(12),
        (MainRect.Width - S(36) - S(12)) / 2,
        ControlBlockHeight);

    private Rectangle PreviewBlockRect => new(
        SourceBlockRect.Right + S(12),
        SourceBlockRect.Y,
        SourceBlockRect.Width,
        SourceBlockRect.Height);

    private bool ShowBench =>
        MainRect.Bottom - S(14) - (SourceBlockRect.Bottom + S(12)) >= S(90);

    private Rectangle BenchRect => new(
        MainRect.X + S(18),
        SourceBlockRect.Bottom + S(12),
        MainRect.Width - S(36),
        S(90));

    private int SourceControlAreaHeight => S((21 * 5) + (3 * 4) + 6);

    private int PreviewControlAreaHeight => S((21 * 6) + (3 * 5) + 6);

    private Rectangle ReenumerateButtonRect => new(
        SourceBlockRect.Right - S(10) - S(78),
        SourceBlockRect.Y + ((S(BlockHeadHeight) - S(22)) / 2),
        S(78),
        S(22));

    private int PillRight => Wide ? RunBarRect.Right : DrawerButtonRect.Left - S(10);

    private Rectangle PauseButtonRect => new(
        PillRight - S(88) - S(8) - S(66),
        RunBarRect.Y + ((RunBarRect.Height - S(24)) / 2),
        S(66),
        S(24));

    private Rectangle SwapButtonRect => new(
        TopologyRect.Right - S(12) - S(92),
        TopologyRect.Y + ((S(BlockHeadHeight) - S(24)) / 2),
        S(92),
        S(24));

    private Rectangle TestContentRect => new(
        MainRect.X + S(18),
        MainRect.Y + S(14),
        MainRect.Width - S(36),
        MainRect.Height - S(28));

    private Rectangle TestConfigRect => new(
        TestContentRect.X,
        TestContentRect.Y,
        TestContentRect.Width,
        S(150));

    private int TestEnvironmentHeight => S(86);

    private Rectangle TestResultRect => new(
        TestContentRect.X,
        TestConfigRect.Bottom + S(12),
        TestContentRect.Width,
        Math.Max(
            S(180),
            TestContentRect.Bottom - (TestConfigRect.Bottom + S(12)) - S(12) - TestEnvironmentHeight));

    private Rectangle TestEnvironmentRect => new(
        TestContentRect.X,
        TestResultRect.Bottom + S(12),
        TestContentRect.Width,
        TestEnvironmentHeight);

    private Rectangle TestStartButtonRect => new(
        TestConfigRect.Right - S(12) - S(170),
        TestConfigRect.Bottom - S(12) - S(28),
        S(170),
        S(28));

    private Rectangle TestDurationRow => new(
        TestConfigRect.X + S(12),
        TestConfigRect.Y + S(BlockHeadHeight) + S(10),
        TestConfigRect.Width - S(24),
        S(22));

    private Rectangle TestReportButtonRect => new(
        TestResultRect.Right - S(12) - S(96),
        TestResultRect.Bottom - S(12) - S(24),
        S(96),
        S(24));

    private Rectangle DiagnosticsDeviceRect => new(
        TestContentRect.X,
        TestContentRect.Y,
        TestContentRect.Width,
        S(252));

    private Rectangle DiagnosticsLogRect => new(
        TestContentRect.X,
        DiagnosticsDeviceRect.Bottom + S(12),
        TestContentRect.Width,
        Math.Max(S(160), TestContentRect.Bottom - DiagnosticsDeviceRect.Bottom - S(12)));

    private Rectangle DiagnosticsExportButtonRect => new(
        DiagnosticsDeviceRect.Right - S(12) - S(118),
        DiagnosticsDeviceRect.Y + ((S(BlockHeadHeight) - S(24)) / 2),
        S(118),
        S(24));

    private Rectangle DiagnosticsLogButtonRect => new(
        DiagnosticsLogRect.Right - S(12) - S(92),
        DiagnosticsLogRect.Y + ((S(BlockHeadHeight) - S(24)) / 2),
        S(92),
        S(24));

    private Rectangle DiagnosticsCopyButtonRect => new(
        DiagnosticsLogRect.Right - S(12) - S(112),
        DiagnosticsLogRect.Bottom - S(10) - S(24),
        S(112),
        S(24));

    private int DiagnosticsLogBodyTop => DiagnosticsLogRect.Y + S(BlockHeadHeight) + S(6);

    private int DiagnosticsLogBodyBottom => DiagnosticsLogRect.Bottom - S(38);

    private int DiagnosticsLogLineHeight => S(15);

    private int DiagnosticsLogVisibleLines =>
        Math.Max(1, (DiagnosticsLogBodyBottom - DiagnosticsLogBodyTop) / DiagnosticsLogLineHeight);

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
        _statusTimer.Enabled = Visible;
        _benchmarkTimer.Enabled = Visible && _benchmarkRunning;
        _animationTimer.Enabled = Visible;
        if (Visible)
        {
            RefreshStats();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        MaybeStartOnboarding();
    }

    private void MaybeStartOnboarding()
    {
        if (_onboardingChecked)
        {
            return;
        }

        _onboardingChecked = true;

        if (_shouldOnboard())
        {
            _onboardingStep = 0;
            _onboardRunStarted = false;
            Invalidate();
        }
    }

    private void SyncStatus()
    {
        PanelStatus status = _statusChannel.Read();
        if (status == _status)
        {
            return;
        }

        // 过渡状态至少停留一段时间再收起，否则设备快速恢复时用户根本看不到发生过什么
        if (status.Kind == PanelStatusKind.Ok
            && _status.Kind != PanelStatusKind.Ok
            && (DateTime.Now - _statusShownAt).TotalMilliseconds < StatusMinVisibleMs)
        {
            return;
        }

        _status = status;
        _statusShownAt = DateTime.Now;
        _switching = status.Kind == PanelStatusKind.Switching;
        Invalidate();
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

            // 收起条远小于常规最小尺寸，不能被最小尺寸约束挡住
            if (_collapsed)
            {
                return;
            }

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
        _pressedCard = HitCard(e.Location)?.Key ?? CardKey.None;
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        CardHit? hit = HitCard(e.Location);
        if (hit is { } card && !_pressedCard.IsNone && card.Key == _pressedCard)
        {
            SelectCard(card);
        }
        else
        {
            PanelAction action = HitAction(e.Location);
            if (action != PanelAction.None && action == _pressed)
            {
                Execute(action);
            }
        }

        _pressed = PanelAction.None;
        _pressedCard = CardKey.None;
        base.OnMouseUp(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        PanelAction hover = HitAction(e.Location);
        CardKey hoverCard = HitCard(e.Location)?.Key ?? CardKey.None;
        if (hover != _hover || hoverCard != _hoverCard)
        {
            _hover = hover;
            _hoverCard = hoverCard;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != PanelAction.None || !_hoverCard.IsNone)
        {
            _hover = PanelAction.None;
            _hoverCard = CardKey.None;
            Invalidate();
        }

        base.OnMouseLeave(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // 只有诊断视图的日志需要滚动，其余视图交给默认处理
        if (_view == PanelView.Diagnostics && _logLines.Length > DiagnosticsLogVisibleLines)
        {
            _logScroll = Math.Clamp(
                _logScroll + (e.Delta > 0 ? 3 : -3),
                0,
                _logLines.Length - DiagnosticsLogVisibleLines);
            Invalidate();
            return;
        }

        base.OnMouseWheel(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.Clear(PanelTheme.Background);

        if (_collapsed)
        {
            DrawCollapsedStrip(graphics);
            return;
        }

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

        if (_onboardingStep >= 0)
        {
            DrawOnboarding(graphics);
        }
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

        if (_view == PanelView.Diagnostics && Visible)
        {
            LoadDiagnostics(force: false);
        }

        Invalidate();
    }

    /// <summary>
    /// 只在监看页且链路有帧时推进黄点，其他页面/暂停/收起/引导期间不产生重绘。
    /// 0.035/60ms 约 1.7 秒走完一遍，三个点等距给出流动方向。
    /// </summary>
    private void AdvanceLinkAnimation()
    {
        if (!_live
            || _view != PanelView.Monitor
            || _collapsed
            || _onboardingStep >= 0
            || WindowState == FormWindowState.Minimized)
        {
            return;
        }

        _linkPhase += 0.035;
        if (_linkPhase >= 1)
        {
            _linkPhase -= 1;
        }

        Invalidate();
    }

    private void LoadDiagnostics(bool force)
    {
        _diagnosticsSnapshot = _diagnostics();

        // 日志每 2 秒重读一次即可，避免每秒打开文件
        if (force || Environment.TickCount64 - _logLoadedAt >= 2000)
        {
            _logLines = _readLogTail(400);
            _logLoadedAt = Environment.TickCount64;
        }

        ClampLogScroll();
    }

    private void ClampLogScroll()
    {
        int maxScroll = Math.Max(0, _logLines.Length - DiagnosticsLogVisibleLines);
        _logScroll = Math.Clamp(_logScroll, 0, maxScroll);
    }

    private string BuildDiagnosticsSummary()
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"Glimpse 诊断摘要 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"状态：{_status.Kind} · {_status.Headline}");
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"实时：cap {_latest.CaptureFps:F1} fps · show {_latest.ShowFps:F1} fps · " +
            $"age p95 {_latest.AgeP95:F1} ms · present p95 {_latest.PresentP95:F1} ms · " +
            $"丢弃 {_latest.Dropped}");
        builder.AppendLine();

        foreach (DiagnosticEntry entry in _diagnosticsSnapshot)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"{entry.Label}：{entry.Value}");
        }

        return builder.ToString();
    }

    private void CopyDiagnosticsSummary()
    {
        try
        {
            Clipboard.SetText(BuildDiagnosticsSummary());
            ShowDiagnosticsNotice("诊断摘要已复制到剪贴板");
        }
        catch (Exception ex)
        {
            ShowDiagnosticsNotice($"复制失败：{ex.Message}");
        }
    }

    private void ExportDiagnosticsPackage()
    {
        string? path = _exportDiagnostics(BuildDiagnosticsSummary());
        ShowDiagnosticsNotice(path is null ? "诊断包导出失败，详见日志" : $"诊断包：{path}");
    }

    private void ShowDiagnosticsNotice(string text)
    {
        _diagnosticsNotice = text;
        _diagnosticsNoticeAt = DateTime.Now;
        Invalidate();
    }

    private void CollapsePanel()
    {
        if (_collapsed)
        {
            return;
        }

        if (WindowState != FormWindowState.Normal)
        {
            WindowState = FormWindowState.Normal;
        }

        _expandedBounds = WindowPlacement.GetBounds(Handle);
        _collapsed = true;
        ApplyCollapsedBounds();
        Invalidate();
    }

    private void ExpandPanel()
    {
        if (!_collapsed)
        {
            return;
        }

        _collapsed = false;
        (int x, int y, int width, int height) = _expandedBounds;

        if (width < S(MinLogicalWidth) || height < S(MinLogicalHeight))
        {
            width = S(DesignWidth);
            height = S(DesignHeight);

            if (CollapsedTargetMonitor() is { } monitor)
            {
                x = monitor.X + ((monitor.Width - width) / 2);
                y = monitor.Y + ((monitor.Height - height) / 2);
            }
        }

        WindowPlacement.Move(Handle, x, y, width, height);
        Invalidate();
    }

    private void ApplyCollapsedBounds()
    {
        if (CollapsedTargetMonitor() is not { } monitor)
        {
            return;
        }

        int width = S(CollapsedWidth);
        int height = S(CollapsedHeight);
        WindowPlacement.Move(
            Handle,
            monitor.X + ((monitor.Width - width) / 2),
            monitor.Y + monitor.Height - height - S(CollapsedBottomMargin),
            width,
            height);
    }

    /// <summary>收起条贴在面板当前所在屏的底边，而不是源屏或预览屏。</summary>
    private MonitorDescriptor? CollapsedTargetMonitor()
    {
        (int x, int y, int width, int height) = WindowPlacement.GetBounds(Handle);
        int centerX = x + (width / 2);
        int centerY = y + (height / 2);
        IReadOnlyList<MonitorDescriptor> monitors = _monitors();

        foreach (MonitorDescriptor monitor in monitors)
        {
            if (centerX >= monitor.X
                && centerX < monitor.X + monitor.Width
                && centerY >= monitor.Y
                && centerY < monitor.Y + monitor.Height)
            {
                return monitor;
            }
        }

        return monitors.Count > 0 ? monitors[0] : null;
    }

    private void HandleDpiChanged(int wParam, IntPtr suggestedRectPointer)
    {
        int newDpi = wParam & 0xFFFF;
        if (newDpi > 0)
        {
            _dpi = newDpi;
        }

        // 收起条不跟随系统建议矩形，按新 DPI 重新贴到当前屏底边
        if (_collapsed)
        {
            ApplyCollapsedBounds();
            Invalidate();
            return;
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
        // 收起态整条都可点，不提供边缘缩放
        if (_collapsed)
        {
            return new IntPtr(HtClient);
        }

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
        // 引导覆盖层存在时，面板其余部分不接受操作
        if (_onboardingStep >= 0)
        {
            return HitOnboarding(point);
        }

        // 收起态只留一条细条，点任意处都展开回主控面板
        if (_collapsed)
        {
            return PanelAction.Expand;
        }

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

        for (int index = 0; index < 3; index++)
        {
            if (RailButtonRect(index).Contains(point))
            {
                return index switch
                {
                    0 => PanelAction.ViewMonitor,
                    1 => PanelAction.ViewTest,
                    _ => PanelAction.ViewDiagnostics,
                };
            }
        }

        if (RailCollapseButtonRect.Contains(point))
        {
            return PanelAction.Collapse;
        }

        if (_view == PanelView.Monitor && ShowBench && BenchRunButtonRect.Contains(point))
        {
            return PanelAction.RunBenchmark;
        }

        if (_view == PanelView.Monitor)
        {
            PanelAction? sourceAction = SourceControlHit(point);
            if (sourceAction is { } sourceHit)
            {
                return sourceHit;
            }

            PanelAction? previewAction = PreviewControlHit(point);
            if (previewAction is { } action)
            {
                return action;
            }
        }

        if (_view == PanelView.Test)
        {
            if (TestStartButtonRect.Contains(point))
            {
                return _benchmarkRunning
                    ? PanelAction.StopBenchmark
                    : PanelAction.StartBenchmark;
            }

            Rectangle[] chips = TestDurationChipRects();
            for (int index = 0; index < chips.Length; index++)
            {
                if (chips[index].Contains(point))
                {
                    return TestDurationActions[index];
                }
            }

            if (TestReportButtonRect.Contains(point))
            {
                return PanelAction.OpenLog;
            }
        }

        if (_view == PanelView.Diagnostics)
        {
            if (DiagnosticsExportButtonRect.Contains(point))
            {
                return PanelAction.ExportDiagnostics;
            }

            if (DiagnosticsLogButtonRect.Contains(point))
            {
                return PanelAction.OpenLog;
            }

            if (DiagnosticsCopyButtonRect.Contains(point))
            {
                return PanelAction.CopyDiagnostics;
            }
        }

        if (_view == PanelView.Monitor && !_switching && SwapButtonRect.Contains(point))
        {
            return PanelAction.SwapDirection;
        }

        if (_view == PanelView.Monitor && ReenumerateButtonRect.Contains(point))
        {
            return PanelAction.Reenumerate;
        }

        if (_view == PanelView.Monitor && PauseButtonRect.Contains(point))
        {
            return PanelAction.TogglePause;
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

    private PanelAction HitOnboarding(Point point)
    {
        if (OnboardSkipRect.Contains(point))
        {
            return PanelAction.OnboardSkip;
        }

        if (_onboardingStep > 0 && OnboardPrevRect.Contains(point))
        {
            return PanelAction.OnboardPrev;
        }

        if (OnboardNextRect.Contains(point))
        {
            if (_onboardingStep < 2)
            {
                return PanelAction.OnboardNext;
            }

            if (_benchmarkRunning)
            {
                return PanelAction.None;
            }

            return _onboardRunStarted ? PanelAction.OnboardFinish : PanelAction.OnboardStart;
        }

        if (_onboardingStep == 0 && OnboardSwapRect.Contains(point))
        {
            return PanelAction.SwapDirection;
        }

        return PanelAction.None;
    }

    private void Execute(PanelAction action)
    {
        switch (action)
        {
            // 关闭收进托盘；最小化与普通窗口一致，落在任务栏
            case PanelAction.Close:
                Hide();
                break;

            case PanelAction.Minimize:
                WindowState = FormWindowState.Minimized;
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
                _logScroll = 0;
                LoadDiagnostics(force: true);
                break;

            case PanelAction.OpenLog:
                _openLogDirectory();
                break;

            case PanelAction.CopyDiagnostics:
                CopyDiagnosticsSummary();
                break;

            case PanelAction.ExportDiagnostics:
                ExportDiagnosticsPackage();
                break;

            case PanelAction.Collapse:
                CollapsePanel();
                break;

            case PanelAction.Expand:
                ExpandPanel();
                break;

            case PanelAction.OnboardSkip:
            case PanelAction.OnboardFinish:
                FinishOnboarding();
                break;

            case PanelAction.OnboardPrev:
                _onboardingStep = Math.Max(0, _onboardingStep - 1);
                break;

            case PanelAction.OnboardNext:
                _onboardingStep = Math.Min(2, _onboardingStep + 1);
                break;

            case PanelAction.OnboardStart:
                StartOnboardBenchmark();
                break;

            case PanelAction.ToggleDrawer:
                _drawerOpen = !_drawerOpen;
                break;

            case PanelAction.RunBenchmark:
                _runBenchmark();
                break;

            case PanelAction.SwapDirection:
                _swapDirection();
                break;

            case PanelAction.Reenumerate:
                _reenumerate();
                break;

            case PanelAction.TogglePause:
                _togglePause();
                break;

            case PanelAction.ToggleCursorCapture:
                _setCursorCaptured(!_isCursorCaptured());
                break;

            case PanelAction.ToggleSelfCapture:
                _setSelfCaptured(!_isSelfCaptured());
                break;

            case PanelAction.TogglePreview:
                _togglePreview();
                break;

            case PanelAction.ToggleTopMost:
                _setTopMost(!_isTopMost());
                break;

            case PanelAction.ToggleOverlay:
                _toggleOverlay();
                break;

            case PanelAction.Scale25:
                _setPreviewScale(25);
                break;

            case PanelAction.Scale50:
                _setPreviewScale(50);
                break;

            case PanelAction.Scale75:
                _setPreviewScale(75);
                break;

            case PanelAction.Scale100:
                _setPreviewScale(100);
                break;

            case PanelAction.AnchorTopRight:
                _setPreviewAnchor(PreviewAnchor.TopRight);
                break;

            case PanelAction.AnchorBottomRight:
                _setPreviewAnchor(PreviewAnchor.BottomRight);
                break;

            case PanelAction.AnchorCenter:
                _setPreviewAnchor(PreviewAnchor.Center);
                break;

            case PanelAction.AnchorFree:
                _setPreviewAnchor(PreviewAnchor.Free);
                break;

            case PanelAction.StartBenchmark:
                StartBenchmark();
                break;

            case PanelAction.StopBenchmark:
                _stopBenchmark();
                break;

            case PanelAction.Duration15:
                _setTestDuration(15);
                break;

            case PanelAction.Duration60:
                _setTestDuration(60);
                break;

            case PanelAction.Duration300:
                _setTestDuration(300);
                break;
        }

        _hover = PanelAction.None;
        _pressed = PanelAction.None;
        Invalidate();
    }

    private static readonly int[] ScalePercents = [25, 50, 75, 100];
    private static readonly PanelAction[] ScaleActions =
        [PanelAction.Scale25, PanelAction.Scale50, PanelAction.Scale75, PanelAction.Scale100];
    private static readonly string[] ScaleLabels = ["25%", "50%", "75%", "100%"];

    private static readonly int[] TestDurations = [15, 60, 300];
    private static readonly PanelAction[] TestDurationActions =
        [PanelAction.Duration15, PanelAction.Duration60, PanelAction.Duration300];
    private static readonly string[] TestDurationLabels = ["15 秒", "60 秒", "5 分"];

    private static readonly (PreviewAnchor Anchor, PanelAction Action, string Label)[] AnchorChips =
    [
        (PreviewAnchor.TopRight, PanelAction.AnchorTopRight, "右上"),
        (PreviewAnchor.BottomRight, PanelAction.AnchorBottomRight, "右下"),
        (PreviewAnchor.Center, PanelAction.AnchorCenter, "居中"),
        (PreviewAnchor.Free, PanelAction.AnchorFree, "自由"),
    ];

    private int ControlRowsTop(Rectangle block, bool sourceSide)
    {
        int reserved = sourceSide ? SourceControlAreaHeight : PreviewControlAreaHeight;
        List<(MonitorDescriptor Monitor, Rectangle Bounds)> cards =
            LayoutCards(block, sourceSide, reserved);
        int top = cards.Count > 0
            ? cards[^1].Bounds.Bottom
            : block.Y + S(BlockHeadHeight) + S(8);
        return top + S(8);
    }

    private Rectangle SourceRowRect(int index) => new(
        SourceBlockRect.X + S(10),
        ControlRowsTop(SourceBlockRect, sourceSide: true) + (index * (S(21) + S(3))),
        SourceBlockRect.Width - S(20),
        S(21));

    private Rectangle PreviewRowRect(int index) => new(
        PreviewBlockRect.X + S(10),
        ControlRowsTop(PreviewBlockRect, sourceSide: false) + (index * (S(21) + S(3))),
        PreviewBlockRect.Width - S(20),
        S(21));

    private Rectangle ToggleRect(Rectangle row) => new(
        row.Right - S(32),
        row.Y + ((row.Height - S(16)) / 2),
        S(32),
        S(16));

    private Rectangle[] ChipRects(Rectangle row, int count, int chipWidth)
    {
        int gap = S(3);
        int total = (count * chipWidth) + ((count - 1) * gap);
        int x = row.Right - total;
        var rects = new Rectangle[count];

        for (int index = 0; index < count; index++)
        {
            rects[index] = new Rectangle(x, row.Y, chipWidth, row.Height);
            x += chipWidth + gap;
        }

        return rects;
    }

    private PanelAction? SourceControlHit(Point point)
    {
        if (ToggleRect(SourceRowRect(0)).Contains(point))
        {
            return PanelAction.ToggleCursorCapture;
        }

        if (ToggleRect(SourceRowRect(1)).Contains(point))
        {
            return PanelAction.ToggleSelfCapture;
        }

        return null;
    }

    private PanelAction? PreviewControlHit(Point point)
    {
        if (ToggleRect(PreviewRowRect(0)).Contains(point))
        {
            return PanelAction.TogglePreview;
        }

        if (ToggleRect(PreviewRowRect(1)).Contains(point))
        {
            return PanelAction.ToggleOverlay;
        }

        if (ToggleRect(PreviewRowRect(2)).Contains(point))
        {
            return PanelAction.ToggleTopMost;
        }

        Rectangle[] scaleChips = ChipRects(PreviewRowRect(3), ScaleActions.Length, S(40));
        for (int index = 0; index < scaleChips.Length; index++)
        {
            if (scaleChips[index].Contains(point))
            {
                return ScaleActions[index];
            }
        }

        Rectangle[] anchorChips = ChipRects(PreviewRowRect(4), AnchorChips.Length, S(46));
        for (int index = 0; index < anchorChips.Length; index++)
        {
            if (anchorChips[index].Contains(point))
            {
                return AnchorChips[index].Action;
            }
        }

        return null;
    }

    private Rectangle[] TestDurationChipRects()
    {
        Rectangle row = TestDurationRow;
        int chipWidth = S(64);
        int gap = S(3);
        var rects = new Rectangle[TestDurations.Length];
        int x = row.X + S(74);

        for (int index = 0; index < rects.Length; index++)
        {
            rects[index] = new Rectangle(x, row.Y, chipWidth, row.Height);
            x += chipWidth + gap;
        }

        return rects;
    }

    private void StartBenchmark()
    {
        if (_benchmarkRunning)
        {
            return;
        }

        _benchmarkRunning = true;
        _benchmarkStartedAt = DateTime.UtcNow;
        _benchmarkRunSeconds = _testDuration();
        _benchmarkTimer.Enabled = Visible;
        _startBenchmark(_benchmarkRunSeconds, OnBenchmarkCompleted);
        Invalidate();
    }

    private void StartOnboardBenchmark()
    {
        if (_benchmarkRunning)
        {
            return;
        }

        _onboardRunStarted = true;
        _benchmarkRunning = true;
        _benchmarkStartedAt = DateTime.UtcNow;
        _benchmarkRunSeconds = 10;
        _benchmarkTimer.Enabled = Visible;
        _startBenchmark(10, OnBenchmarkCompleted);
        Invalidate();
    }

    private void FinishOnboarding()
    {
        _onboardingStep = -1;
        _onboardRunStarted = false;
        _completeOnboarding();
        Invalidate();
    }

    private void OnBenchmarkCompleted(BenchmarkResult? result, string? reportPath)
    {
        _benchmarkRunning = false;
        _benchmarkTimer.Enabled = false;

        if (result is not null)
        {
            _lastBenchmark = result;
            _lastReportPath = reportPath;
        }

        RefreshNow();
    }

    private static string FormatDuration(int seconds) =>
        seconds >= 300 ? "5 分" : $"{seconds} 秒";

    private void SelectCard(CardHit card)
    {
        if (card.Key.Source)
        {
            _selectSource(card.Monitor);
        }
        else
        {
            _selectPreview(card.Monitor);
        }

        RefreshStats();
    }

    private CardHit? HitCard(Point point)
    {
        // 切换过程中忽略卡片点击，避免在上一次切换未收敛时叠加新请求
        if (_view != PanelView.Monitor || _switching)
        {
            return null;
        }

        foreach ((MonitorDescriptor monitor, Rectangle bounds) in LayoutCards(SourceBlockRect, sourceSide: true))
        {
            if (bounds.Contains(point))
            {
                return new CardHit(new CardKey(monitor.Index, true), monitor);
            }
        }

        foreach ((MonitorDescriptor monitor, Rectangle bounds) in LayoutCards(PreviewBlockRect, sourceSide: false))
        {
            if (bounds.Contains(point))
            {
                return new CardHit(new CardKey(monitor.Index, false), monitor);
            }
        }

        return null;
    }

    private List<(MonitorDescriptor Monitor, Rectangle Bounds)> LayoutCards(
        Rectangle block,
        bool sourceSide,
        int reservedBottom = 0)
    {
        var cards = new List<(MonitorDescriptor, Rectangle)>();
        IReadOnlyList<MonitorDescriptor> monitors = _monitors();
        MonitorDescriptor selected = sourceSide ? _sourceMonitor() : _previewMonitor();

        var body = new Rectangle(
            block.X + S(10),
            block.Y + S(BlockHeadHeight) + S(8),
            block.Width - S(20),
            block.Height - S(BlockHeadHeight) - S(16) - reservedBottom);

        int y = body.Y;
        foreach (MonitorDescriptor monitor in monitors)
        {
            bool isSelected = IsSameMonitor(monitor, selected);
            int height = S(isSelected ? 56 : 40);
            if (y + height > body.Bottom)
            {
                break;
            }

            cards.Add((monitor, new Rectangle(body.X, y, body.Width, height)));
            y += height + S(6);
        }

        return cards;
    }

    private static bool IsSameMonitor(MonitorDescriptor left, MonitorDescriptor right) =>
        left.Index == right.Index
        || (!string.IsNullOrEmpty(left.DeviceName)
            && string.Equals(left.DeviceName, right.DeviceName, StringComparison.OrdinalIgnoreCase));

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
        DrawRailCollapseButton(graphics);
    }

    private void DrawRailCollapseButton(Graphics graphics)
    {
        Rectangle rect = RailCollapseButtonRect;
        bool hover = _hover == PanelAction.Collapse;

        if (hover)
        {
            using var brush = new SolidBrush(PanelTheme.Surface);
            graphics.FillRectangle(brush, rect);

            using var border = new Pen(PanelTheme.Line, PenWidth(1));
            graphics.DrawRectangle(border, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }

        Color color = hover ? PanelTheme.Foreground2 : PanelTheme.Foreground3;
        int centerX = rect.X + (rect.Width / 2);
        int centerY = rect.Y + S(16);

        using (var pen = new Pen(color, PenWidth(1.3f)))
        {
            graphics.DrawLine(pen, centerX - S(5), centerY + S(3), centerX, centerY - S(2));
            graphics.DrawLine(pen, centerX + S(5), centerY + S(3), centerX, centerY - S(2));
        }

        DrawText(
            graphics,
            "收起",
            PanelTheme.Small,
            color,
            new Rectangle(rect.X, rect.Y + S(30), rect.Width, S(14)),
            StringAlignment.Center,
            StringAlignment.Center,
            ellipsis: false);
    }

    private void DrawCollapsedStrip(Graphics graphics)
    {
        int width = ClientSize.Width;
        int height = ClientSize.Height;
        DrawLine(graphics, 0, 0, width, 0, PanelTheme.Line2);
        DrawLine(graphics, 0, height - 1, width, height - 1, PanelTheme.Line2);

        Color dotColor = _status.Kind switch
        {
            PanelStatusKind.Error => PanelTheme.Error,
            PanelStatusKind.Switching => PanelTheme.Amber,
            PanelStatusKind.Paused => PanelTheme.Foreground3,
            _ => PanelTheme.Ok,
        };

        int dotSize = S(8);
        int x = S(16);
        using (var brush = new SolidBrush(dotColor))
        {
            graphics.FillEllipse(brush, x, (height - dotSize) / 2, dotSize, dotSize);
        }

        x += dotSize + S(8);
        MonitorDescriptor source = _sourceMonitor();
        MonitorDescriptor preview = _previewMonitor();
        string route =
            $"{(source.IsPrimary ? "主屏" : "副屏")} {source.Index} → " +
            $"{(preview.IsPrimary ? "主屏" : "副屏")} {preview.Index}";

        DrawText(
            graphics,
            route,
            PanelTheme.Body,
            PanelTheme.Foreground2,
            new Rectangle(x, 0, S(152), height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        x += S(152) + S(4);
        DrawLine(graphics, x, S(14), x, height - S(14), PanelTheme.Line2);
        x += S(12);

        DrawText(
            graphics,
            $"{_latest.CaptureFps:F0} fps",
            PanelTheme.MonoSmall,
            PanelTheme.Foreground,
            new Rectangle(x, 0, S(56), height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        x += S(60);
        DrawText(
            graphics,
            $"{_latest.AgeP95:F0} ms",
            PanelTheme.MonoSmall,
            PanelTheme.Foreground,
            new Rectangle(x, 0, S(56), height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        // 点击穿透在当前呈现路径下不可用，收起条上只作禁用态展示
        DrawText(
            graphics,
            "穿透",
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(CollapsedToggleRect.X - S(40), 0, S(36), height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);
        DrawDisabledToggle(graphics, CollapsedToggleRect);

        DrawQuietButton(graphics, CollapsedExpandButtonRect, "展开", PanelAction.Expand);
    }

    private void DrawDisabledToggle(Graphics graphics, Rectangle rect)
    {
        using (var path = Rounded(rect, rect.Height / 2))
        {
            using var brush = new SolidBrush(PanelTheme.Surface2);
            graphics.FillPath(brush, path);

            using var pen = new Pen(PanelTheme.Line2, PenWidth(1));
            graphics.DrawPath(pen, path);
        }

        int knob = S(11);
        using var knobBrush = new SolidBrush(PanelTheme.Line3);
        graphics.FillEllipse(
            knobBrush,
            rect.X + S(2),
            rect.Y + ((rect.Height - knob) / 2),
            knob,
            knob);
    }

    private void DrawOnboarding(Graphics graphics)
    {
        using (var veil = new SolidBrush(Color.FromArgb(196, 5, 6, 8)))
        {
            graphics.FillRectangle(veil, ClientRectangle);
        }

        Rectangle dialog = OnboardDialogRect;
        DrawBlock(graphics, dialog);

        string title = _onboardingStep switch
        {
            0 => "先确认两块屏",
            1 => "自动检查",
            _ => "试运行 10 秒",
        };

        DrawText(
            graphics,
            title,
            PanelTheme.BodyStrong,
            PanelTheme.Foreground,
            new Rectangle(dialog.X + S(20), dialog.Y, dialog.Width - S(160), S(BlockHeadHeight)),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        // 步骤指示：已完成为绿、当前为琥珀、未开始为灰
        for (int index = 0; index < 3; index++)
        {
            int size = S(18);
            var step = new Rectangle(
                dialog.Right - S(20) - ((3 - index) * (size + S(6))),
                dialog.Y + ((S(BlockHeadHeight) - size) / 2),
                size,
                size);

            bool done = index < _onboardingStep;
            bool active = index == _onboardingStep;

            using (var path = Rounded(step, size / 2))
            {
                using var brush = new SolidBrush(
                    active ? PanelTheme.Amber : done ? PanelTheme.Ok : PanelTheme.Surface2);
                graphics.FillPath(brush, path);
            }

            DrawText(
                graphics,
                $"{index + 1}",
                PanelTheme.Small,
                active || done ? PanelTheme.AmberInk : PanelTheme.Foreground3,
                step,
                StringAlignment.Center,
                StringAlignment.Center,
                ellipsis: false);
        }

        DrawLine(
            graphics,
            dialog.X,
            dialog.Y + S(BlockHeadHeight),
            dialog.Right,
            dialog.Y + S(BlockHeadHeight),
            PanelTheme.Line);

        var body = new Rectangle(
            dialog.X + S(20),
            dialog.Y + S(BlockHeadHeight) + S(14),
            dialog.Width - S(40),
            dialog.Height - S(BlockHeadHeight) - S(74));

        switch (_onboardingStep)
        {
            case 0:
                DrawOnboardScreens(graphics, body);
                break;

            case 1:
                DrawOnboardChecks(graphics, body);
                break;

            default:
                DrawOnboardRun(graphics, body);
                break;
        }

        DrawQuietButton(graphics, OnboardSkipRect, "跳过引导", PanelAction.OnboardSkip);

        if (_onboardingStep > 0)
        {
            DrawQuietButton(graphics, OnboardPrevRect, "上一步", PanelAction.OnboardPrev);
        }

        DrawOnboardNextButton(graphics);
    }

    private void DrawOnboardScreens(Graphics graphics, Rectangle body)
    {
        DrawText(
            graphics,
            "选中要监看的屏和显示预览的屏，方向反了用右上角「交换方向」即可。",
            PanelTheme.Body,
            PanelTheme.Foreground2,
            new Rectangle(body.X, body.Y, body.Width, S(24)),
            StringAlignment.Near,
            StringAlignment.Center);

        MonitorDescriptor source = _sourceMonitor();
        MonitorDescriptor preview = _previewMonitor();
        int cardWidth = Math.Min(S(320), (body.Width / 2) - S(12));
        int cardHeight = S(84);

        var sourceCard = new Rectangle(body.X, body.Y + S(44), cardWidth, cardHeight);
        DrawMonitorCard(graphics, sourceCard, source, selected: true, hovered: false, dimmed: false);

        DrawText(
            graphics,
            "预览显示在",
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(body.X + S(20), sourceCard.Bottom + S(4), S(120), S(24)),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        var previewCard = new Rectangle(body.X, sourceCard.Bottom + S(34), cardWidth, cardHeight);
        DrawMonitorCard(graphics, previewCard, preview, selected: true, hovered: false, dimmed: false);

        DrawText(
            graphics,
            "选好后点「下一步」。之后随时可以在监看页调整。",
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(body.X + cardWidth + S(24), body.Y + S(60), body.Width - cardWidth - S(48), S(40)),
            StringAlignment.Near,
            StringAlignment.Center);

        DrawQuietButton(graphics, OnboardSwapRect, "交换方向", PanelAction.SwapDirection);
    }

    private void DrawOnboardChecks(Graphics graphics, Rectangle body)
    {
        MonitorDescriptor source = _sourceMonitor();
        MonitorDescriptor preview = _previewMonitor();
        bool captureOk = _status.Kind != PanelStatusKind.Error;

        (string Label, string Value, bool Ok)[] checks =
        [
            ("窗口捕获可用", captureOk ? "WGC · 运行中" : _status.Headline, captureOk),
            (
                "适配器",
                $"{_adapterName()} · {(_adapterFallback() ? "已降级" : "高性能")}",
                !_adapterFallback()),
            ("预览窗已排除出捕获", _captureExcluded() ? "0x11" : "会出现在捕获中", _captureExcluded()),
            ("热键", $"暂停 {string.Join("+", _pauseShortcutKeys)}", true),
            ("链路", $"#{source.Index} → #{preview.Index}", true),
        ];

        int rowHeight = S(40);
        int y = body.Y;

        foreach ((string label, string value, bool ok) in checks)
        {
            Color color = ok ? PanelTheme.Ok : PanelTheme.Warn;
            DrawText(
                graphics,
                ok ? "✓" : "!",
                PanelTheme.BodyStrong,
                color,
                new Rectangle(body.X, y, S(20), rowHeight),
                StringAlignment.Center,
                StringAlignment.Center,
                ellipsis: false);

            DrawText(
                graphics,
                label,
                PanelTheme.Body,
                PanelTheme.Foreground2,
                new Rectangle(body.X + S(28), y, body.Width - S(300), rowHeight),
                StringAlignment.Near,
                StringAlignment.Center,
                ellipsis: false);

            DrawText(
                graphics,
                value,
                PanelTheme.MonoSmall,
                ok ? PanelTheme.Foreground : PanelTheme.Warn,
                new Rectangle(body.Right - S(260), y, S(260), rowHeight),
                StringAlignment.Far,
                StringAlignment.Center);

            y += rowHeight;
        }

        DrawText(
            graphics,
            "跨适配器不是错误，只是延迟会高一点；想追更低延迟，把预览窗拖到同一块显卡驱动的屏上。",
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(body.X, y + S(6), body.Width, S(40)),
            StringAlignment.Near,
            StringAlignment.Center);
    }

    private void DrawOnboardRun(Graphics graphics, Rectangle body)
    {
        if (_benchmarkRunning)
        {
            DrawTestProgress(graphics, body);
            return;
        }

        if (_onboardRunStarted && _lastBenchmark is { } result)
        {
            DrawTestTable(graphics, body, result);
            return;
        }

        DrawText(
            graphics,
            "运行 10 秒确认捕获与呈现链路稳定。测试期间预览正常显示，完成后自动写入基准报告。",
            PanelTheme.Body,
            PanelTheme.Foreground2,
            new Rectangle(body.X, body.Y + S(20), body.Width, S(60)),
            StringAlignment.Near,
            StringAlignment.Center);
    }

    private void DrawOnboardNextButton(Graphics graphics)
    {
        string text;
        PanelAction action;
        bool enabled = true;

        if (_onboardingStep < 2)
        {
            text = "下一步";
            action = PanelAction.OnboardNext;
        }
        else if (_benchmarkRunning)
        {
            text = "运行中…";
            action = PanelAction.OnboardStart;
            enabled = false;
        }
        else if (_onboardRunStarted)
        {
            text = "完成";
            action = PanelAction.OnboardFinish;
        }
        else
        {
            text = "开始试运行 10 秒";
            action = PanelAction.OnboardStart;
        }

        DrawPrimaryButton(graphics, OnboardNextRect, text, action, enabled);
    }

    private void DrawPrimaryButton(
        Graphics graphics,
        Rectangle rect,
        string text,
        PanelAction action,
        bool enabled)
    {
        bool hover = enabled && _hover == action;
        using var path = Rounded(rect, S(2));

        using (var brush = new SolidBrush(
            !enabled ? PanelTheme.Surface2 : hover ? Color.FromArgb(255, 188, 85) : PanelTheme.Amber))
        {
            graphics.FillPath(brush, path);
        }

        if (!enabled)
        {
            using var pen = new Pen(PanelTheme.Line2, PenWidth(1));
            graphics.DrawPath(pen, path);
        }

        DrawText(
            graphics,
            text,
            PanelTheme.BodyStrong,
            enabled ? PanelTheme.AmberInk : PanelTheme.Foreground3,
            rect,
            StringAlignment.Center,
            StringAlignment.Center,
            ellipsis: false);
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
            default:
                DrawPulseIcon(graphics, iconRect, color);
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
                DrawMonitorControls(graphics);
                DrawBenchSummary(graphics);
                break;

            case PanelView.Test:
                DrawTestView(graphics);
                break;

            default:
                DrawDiagnosticsView(graphics);
                break;
        }

        graphics.Restore(state);
    }

    private void DrawRunBar(Graphics graphics)
    {
        Rectangle bar = RunBarRect;

        string headline;
        Color headlineColor;
        Color dotColor;

        if (_status.Kind == PanelStatusKind.Paused)
        {
            headline = "已暂停";
            headlineColor = PanelTheme.Warn;
            dotColor = PanelTheme.Foreground3;
        }
        else if (_status.Kind == PanelStatusKind.Error)
        {
            headline = _status.Headline;
            headlineColor = PanelTheme.Error;
            dotColor = PanelTheme.Error;
        }
        else if (_status.Kind == PanelStatusKind.Switching)
        {
            headline = _status.Headline;
            headlineColor = PanelTheme.Amber;
            dotColor = PanelTheme.Amber;
        }
        else if (_live)
        {
            headline = "正在监看";
            headlineColor = PanelTheme.Foreground;
            dotColor = PanelTheme.Ok;
        }
        else if (_latest.LastFrameAt is not null)
        {
            headline = "无新帧";
            headlineColor = PanelTheme.Warn;
            dotColor = PanelTheme.Warn;
        }
        else
        {
            headline = "源屏无画面";
            headlineColor = PanelTheme.Error;
            dotColor = PanelTheme.Error;
        }

        int dot = S(7);
        using (var brush = new SolidBrush(dotColor))
        {
            graphics.FillEllipse(brush, bar.X, bar.Y + ((bar.Height - dot) / 2), dot, dot);
        }

        int textX = bar.X + S(17);
        int headlineWidth = S(178);
        DrawText(
            graphics,
            headline,
            PanelTheme.BodyStrong,
            headlineColor,
            new Rectangle(textX, bar.Y, headlineWidth, bar.Height),
            StringAlignment.Near,
            StringAlignment.Center);

        MonitorDescriptor source = _sourceMonitor();
        MonitorDescriptor preview = _previewMonitor();
        string route =
            $"捕获 #{source.Index} {source.Width}×{source.Height} → 预览 #{preview.Index} {preview.Width}×{preview.Height}";

        if (!Wide)
        {
            DrawQuietButton(graphics, DrawerButtonRect, "读数", PanelAction.ToggleDrawer);
        }

        // 运行条上的主操作：暂停 / 继续；点击穿透因架构限制暂未接入
        DrawQuietButton(
            graphics,
            PauseButtonRect,
            _isPaused() ? "继续" : "暂停",
            PanelAction.TogglePause);

        int routeX = textX + headlineWidth + S(10);
        int routeWidth = Math.Max(0, PauseButtonRect.Left - S(10) - routeX);
        DrawText(
            graphics,
            route,
            PanelTheme.MonoSmall,
            PanelTheme.Foreground3,
            new Rectangle(routeX, bar.Y, routeWidth, bar.Height),
            StringAlignment.Near,
            StringAlignment.Center);

        bool excluded = _captureExcluded();
        var pill = new Rectangle(PillRight - S(88), bar.Y + ((bar.Height - S(19)) / 2), S(88), S(19));
        DrawPill(graphics, pill, excluded ? "捕获隐身" : "未隐身", excluded ? PanelTheme.Ok : PanelTheme.Warn);

        DrawLine(graphics, bar.X, bar.Bottom, bar.Right - 1, bar.Bottom, PanelTheme.Line);
        DrawStatusBanner(graphics);
    }

    private void DrawStatusBanner(Graphics graphics)
    {
        if (!ShowBanner)
        {
            return;
        }

        Rectangle banner = BannerRect;
        Color accent = _status.Kind == PanelStatusKind.Error ? PanelTheme.Error : PanelTheme.Amber;

        using (var path = Rounded(banner, S(2)))
        {
            using (var fill = new SolidBrush(PanelTheme.Surface))
            {
                graphics.FillPath(fill, path);
            }

            using var pen = new Pen(Color.FromArgb(110, accent), PenWidth(1));
            graphics.DrawPath(pen, path);
        }

        using (var barBrush = new SolidBrush(accent))
        {
            graphics.FillRectangle(barBrush, banner.X + 1, banner.Y + 1, S(3), banner.Height - 2);
        }

        int dot = S(6);
        using (var dotBrush = new SolidBrush(accent))
        {
            graphics.FillEllipse(dotBrush, banner.X + S(12), banner.Y + ((banner.Height - dot) / 2), dot, dot);
        }

        int textX = banner.X + S(26);
        int detailWidth = banner.Width - S(38);
        bool hasDetail = !string.IsNullOrWhiteSpace(_status.Detail);

        DrawText(
            graphics,
            _status.Headline,
            PanelTheme.BodyStrong,
            accent,
            new Rectangle(textX, banner.Y, S(238), banner.Height),
            StringAlignment.Near,
            StringAlignment.Center);

        if (hasDetail)
        {
            DrawText(
                graphics,
                _status.Detail!,
                PanelTheme.Small,
                PanelTheme.Foreground3,
                new Rectangle(textX + S(244), banner.Y, Math.Max(0, detailWidth - S(244)), banner.Height),
                StringAlignment.Near,
                StringAlignment.Center);
        }

        int seconds = (int)Math.Max(0, (DateTime.Now - _status.UpdatedAt).TotalSeconds);
        DrawText(
            graphics,
            $"{seconds}s",
            PanelTheme.MonoSmall,
            PanelTheme.Foreground3,
            new Rectangle(banner.Right - S(44), banner.Y, S(34), banner.Height),
            StringAlignment.Far,
            StringAlignment.Center,
            ellipsis: false);
    }

    private void DrawTopology(Graphics graphics)
    {
        Rectangle block = TopologyRect;
        DrawBlock(graphics, block);
        DrawBlockHead(graphics, block, "投屏链路", right: null);
        DrawQuietButton(graphics, SwapButtonRect, "交换方向", PanelAction.SwapDirection);

        Rectangle body = new(block.X + 1, block.Y + S(BlockHeadHeight), block.Width - 2, block.Height - S(BlockHeadHeight) - 1);
        DrawGrid(graphics, body, S(28), Color.FromArgb(20, PanelTheme.Line));

        MonitorDescriptor source = _sourceMonitor();
        MonitorDescriptor preview = _previewMonitor();

        double sourceAspect = source.Width / (double)Math.Max(1, source.Height);
        double previewAspect = preview.Width / (double)Math.Max(1, preview.Height);

        int frameHeight = Math.Min(S(150), Math.Max(S(60), body.Height - S(72)));

        // 链路中间要完整放下「WGC · 显存零拷贝 · 0 读回」与流动点，间距按实测文本留余量
        int linkGap = S(196);
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

        if (_adapterFallback())
        {
            // 高性能适配器不可用时会退回显示器所属或首个适配器，延迟可能上升
            int footWidth = (int)Math.Ceiling(Measure(graphics, foot, PanelTheme.MonoSmall).Width);
            DrawText(
                graphics,
                "适配器已降级",
                PanelTheme.MonoSmall,
                PanelTheme.Warn,
                new Rectangle(body.X + S(12) + footWidth + S(12), footY, S(90), S(18)),
                StringAlignment.Near,
                StringAlignment.Center,
                ellipsis: false);
        }

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

        DrawCornerTicks(graphics, frame, PanelTheme.Line3);

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
            int dot = S(4);
            int glow = S(9);

            // 三个黄点等距同向流动，最前面的点最亮，后两个渐隐，直观表示帧从源走向预览
            for (int index = 0; index < 3; index++)
            {
                double phase = _linkPhase + (index / 3.0);
                phase -= Math.Floor(phase);
                int x = startX + (int)Math.Round((endX - startX) * phase);
                int alpha = index switch
                {
                    0 => 255,
                    1 => 150,
                    _ => 80,
                };

                using (var glowBrush = new SolidBrush(
                    Color.FromArgb(index == 0 ? 48 : 20, PanelTheme.Amber)))
                {
                    graphics.FillEllipse(
                        glowBrush,
                        x - (glow / 2),
                        midY - (glow / 2),
                        glow,
                        glow);
                }

                using var brush = new SolidBrush(Color.FromArgb(alpha, PanelTheme.Amber));
                graphics.FillEllipse(brush, x - (dot / 2), midY - (dot / 2), dot, dot);
            }
        }

        string label = _live ? "WGC · 显存零拷贝 · 0 读回" : "链路停止";

        // 按实际文本宽度定矩形，避免居中裁剪把首尾字切掉
        int labelWidth = (int)Math.Ceiling(Measure(graphics, label, PanelTheme.MonoSmall).Width) + S(8);

        DrawText(
            graphics,
            label,
            PanelTheme.MonoSmall,
            _live ? PanelTheme.Foreground3 : PanelTheme.Error,
            new Rectangle(
                ((startX + endX) - labelWidth) / 2,
                midY - S(24),
                labelWidth,
                S(16)),
            StringAlignment.Center,
            StringAlignment.Center,
            ellipsis: false);
    }

    private void DrawBenchSummary(Graphics graphics)
    {
        if (!ShowBench)
        {
            return;
        }

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

    private void DrawMonitorControls(Graphics graphics)
    {
        DrawMonitorBlock(graphics, SourceBlockRect, sourceSide: true, "投屏源");
        DrawMonitorBlock(graphics, PreviewBlockRect, sourceSide: false, "预览输出");
    }

    private void DrawMonitorBlock(Graphics graphics, Rectangle block, bool sourceSide, string title)
    {
        DrawBlock(graphics, block);

        MonitorDescriptor selected = sourceSide ? _sourceMonitor() : _previewMonitor();
        string? headRight = null;
        if (!sourceSide)
        {
            (int width, int height) = _previewSize();
            headRight = $"#{selected.Index} {ShortDeviceName(selected.DeviceName)} · {width}×{height}";
        }

        DrawBlockHead(graphics, block, title, headRight);

        if (sourceSide)
        {
            DrawQuietButton(graphics, ReenumerateButtonRect, "重新枚举", PanelAction.Reenumerate);
        }

        int reserved = sourceSide ? SourceControlAreaHeight : PreviewControlAreaHeight;
        List<(MonitorDescriptor Monitor, Rectangle Bounds)> cards = LayoutCards(block, sourceSide, reserved);
        foreach ((MonitorDescriptor monitor, Rectangle bounds) in cards)
        {
            bool isSelected = IsSameMonitor(monitor, selected);
            bool hovered = _hoverCard == new CardKey(monitor.Index, sourceSide);
            DrawMonitorCard(graphics, bounds, monitor, isSelected, hovered, _switching);
        }

        if (sourceSide)
        {
            DrawSourceControlRows(graphics);
        }
        else
        {
            DrawPreviewControlRows(graphics);
        }
    }

    private void DrawSourceControlRows(Graphics graphics)
    {
        Rectangle cursorRow = SourceRowRect(0);
        DrawPreviewRowLabel(graphics, cursorRow, "捕获鼠标光标");
        DrawToggle(graphics, ToggleRect(cursorRow), _isCursorCaptured(), PanelAction.ToggleCursorCapture);

        Rectangle selfRow = SourceRowRect(1);
        Rectangle selfLabel = new(cursorRow.X, selfRow.Y, selfRow.Width - S(210), selfRow.Height);
        DrawText(
            graphics,
            "捕获自身窗口",
            PanelTheme.Small,
            PanelTheme.Foreground2,
            selfLabel,
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);
        DrawText(
            graphics,
            "以黑块出现",
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(selfLabel.Right + S(6), selfRow.Y, S(88), selfRow.Height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);
        DrawToggle(graphics, ToggleRect(selfRow), _isSelfCaptured(), PanelAction.ToggleSelfCapture);

        DrawInfoRow(graphics, SourceRowRect(2), "采集路径", "WGC · 显存零拷贝", null);
        DrawInfoRow(graphics, SourceRowRect(3), "帧池", "2", null);

        Rectangle stealthRow = SourceRowRect(4);
        DrawText(
            graphics,
            "隐身状态",
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(stealthRow.X, stealthRow.Y, stealthRow.Width / 2, stealthRow.Height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        bool excluded = _captureExcluded();
        var pill = new Rectangle(
            stealthRow.Right - S(122),
            stealthRow.Y + ((stealthRow.Height - S(19)) / 2),
            S(122),
            S(19));
        DrawPill(graphics, pill, excluded ? "已排除出捕获" : "会出现在捕获中", excluded ? PanelTheme.Ok : PanelTheme.Warn);
    }

    private static void DrawInfoRow(Graphics graphics, Rectangle row, string label, string value, Color? valueColor)
    {
        DrawText(
            graphics,
            label,
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(row.X, row.Y, row.Width / 2, row.Height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        DrawText(
            graphics,
            value,
            PanelTheme.MonoSmall,
            valueColor ?? PanelTheme.Foreground2,
            new Rectangle(row.X + (row.Width / 2), row.Y, row.Width / 2, row.Height),
            StringAlignment.Far,
            StringAlignment.Center);
    }

    private void DrawPreviewControlRows(Graphics graphics)
    {
        Rectangle shown = PreviewRowRect(0);
        DrawPreviewRowLabel(graphics, shown, "预览显示");
        DrawToggle(graphics, ToggleRect(shown), _isPreviewVisible(), PanelAction.TogglePreview);

        Rectangle overlay = PreviewRowRect(1);
        DrawPreviewRowLabel(graphics, overlay, "窗口内读数");
        DrawToggle(graphics, ToggleRect(overlay), _isOverlayVisible(), PanelAction.ToggleOverlay);

        Rectangle topMost = PreviewRowRect(2);
        DrawPreviewRowLabel(graphics, topMost, "始终最前");
        DrawToggle(graphics, ToggleRect(topMost), _isTopMost(), PanelAction.ToggleTopMost);

        Rectangle scaleRow = PreviewRowRect(3);
        DrawPreviewRowLabel(graphics, scaleRow, "窗口尺寸");
        int scale = _previewScale();
        Rectangle[] scaleChips = ChipRects(scaleRow, ScaleActions.Length, S(40));
        for (int index = 0; index < scaleChips.Length; index++)
        {
            DrawChip(
                graphics,
                scaleChips[index],
                ScaleLabels[index],
                scale == ScalePercents[index],
                ScaleActions[index]);
        }

        Rectangle anchorRow = PreviewRowRect(4);
        DrawPreviewRowLabel(graphics, anchorRow, "贴边位置");
        PreviewAnchor anchor = _previewAnchor();
        Rectangle[] anchorChips = ChipRects(anchorRow, AnchorChips.Length, S(46));
        for (int index = 0; index < anchorChips.Length; index++)
        {
            (PreviewAnchor value, PanelAction action, string label) = AnchorChips[index];
            DrawChip(graphics, anchorChips[index], label, anchor == value, action);
        }

        Rectangle latencyRow = PreviewRowRect(5);
        DrawPreviewRowLabel(graphics, latencyRow, "帧延迟上限");
        DrawText(
            graphics,
            "1 帧",
            PanelTheme.MonoSmall,
            PanelTheme.Foreground2,
            new Rectangle(latencyRow.X, latencyRow.Y, latencyRow.Width, latencyRow.Height),
            StringAlignment.Far,
            StringAlignment.Center,
            ellipsis: false);
    }

    private void DrawPreviewRowLabel(Graphics graphics, Rectangle row, string label) =>
        DrawText(
            graphics,
            label,
            PanelTheme.Small,
            PanelTheme.Foreground2,
            new Rectangle(row.X, row.Y, row.Width - S(210), row.Height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

    private void DrawToggle(Graphics graphics, Rectangle bounds, bool on, PanelAction action)
    {
        bool hover = _hover == action;
        int trackWidth = S(32);
        int trackHeight = S(16);
        var track = new Rectangle(
            bounds.X + ((bounds.Width - trackWidth) / 2),
            bounds.Y + ((bounds.Height - trackHeight) / 2),
            trackWidth,
            trackHeight);

        using (var path = Rounded(track, trackHeight / 2))
        {
            using (var fill = new SolidBrush(on ? PanelTheme.Amber : PanelTheme.Surface2))
            {
                graphics.FillPath(fill, path);
            }

            Color border = on
                ? PanelTheme.Amber
                : hover ? PanelTheme.Line3 : PanelTheme.Line2;
            using var pen = new Pen(border, PenWidth(1));
            graphics.DrawPath(pen, path);
        }

        int knob = S(10);
        int knobX = on
            ? track.Right - knob - S(3)
            : track.X + S(3);
        using (var brush = new SolidBrush(on ? PanelTheme.AmberInk : PanelTheme.Foreground3))
        {
            graphics.FillEllipse(brush, knobX, track.Y + ((track.Height - knob) / 2), knob, knob);
        }
    }

    private void DrawChip(Graphics graphics, Rectangle bounds, string text, bool active, PanelAction action)
    {
        bool hover = _hover == action;
        using (var path = Rounded(bounds, S(2)))
        {
            using (var fill = new SolidBrush(active ? PanelTheme.Surface2 : PanelTheme.Surface))
            {
                graphics.FillPath(fill, path);
            }

            Color border = active
                ? PanelTheme.Amber
                : hover ? PanelTheme.Line3 : PanelTheme.Line2;
            using var pen = new Pen(border, PenWidth(1));
            graphics.DrawPath(pen, path);
        }

        DrawText(
            graphics,
            text,
            PanelTheme.MonoSmall,
            active ? PanelTheme.Amber : hover ? PanelTheme.Foreground2 : PanelTheme.Foreground3,
            bounds,
            StringAlignment.Center,
            StringAlignment.Center,
            ellipsis: false);
    }

    private void DrawMonitorCard(
        Graphics graphics,
        Rectangle bounds,
        MonitorDescriptor monitor,
        bool selected,
        bool hovered,
        bool dimmed)
    {
        using var path = Rounded(bounds, S(2));
        using (var brush = new SolidBrush(!dimmed && (selected || hovered) ? PanelTheme.Surface2 : PanelTheme.Surface))
        {
            graphics.FillPath(brush, path);
        }

        Color border = dimmed
            ? PanelTheme.Line
            : selected ? PanelTheme.Amber
            : hovered ? PanelTheme.Line3 : PanelTheme.Line;
        using (var pen = new Pen(border, PenWidth(1)))
        {
            graphics.DrawPath(pen, path);
        }

        int thumbHeight = selected ? S(26) : S(18);
        double aspect = monitor.Width / (double)Math.Max(1, monitor.Height);
        int thumbWidth = Math.Min(S(34), Math.Max(S(12), (int)Math.Round(thumbHeight * aspect)));
        var thumb = new Rectangle(
            bounds.X + S(10),
            bounds.Y + ((bounds.Height - thumbHeight) / 2),
            thumbWidth,
            thumbHeight);

        using (var brush = new SolidBrush(Color.FromArgb(10, 12, 15)))
        {
            graphics.FillRectangle(brush, thumb);
        }

        using (var pen = new Pen(dimmed ? PanelTheme.Line2 : selected ? PanelTheme.Amber : PanelTheme.Line3, PenWidth(1)))
        {
            graphics.DrawRectangle(pen, thumb);
        }

        string name = $"{(monitor.IsPrimary ? "主屏" : "副屏")} · {ShortDeviceName(monitor.DeviceName)}";
        string resolution = $"{monitor.Width}×{monitor.Height}";
        int textX = thumb.Right + S(10);
        int textRight = bounds.Right - S(10);

        if (selected)
        {
            DrawText(
                graphics,
                name,
                PanelTheme.Body,
                PanelTheme.Foreground,
                new Rectangle(textX, bounds.Y + S(5), Math.Max(0, textRight - textX - S(20)), S(16)),
                StringAlignment.Near,
                StringAlignment.Center,
                ellipsis: false);

            string meta = $"{resolution} · {(monitor.Width < monitor.Height ? "竖屏" : "横屏")}";
            DrawText(
                graphics,
                meta,
                PanelTheme.MonoSmall,
                PanelTheme.Foreground3,
                new Rectangle(textX, bounds.Y + S(24), Math.Max(0, textRight - textX - S(20)), S(14)),
                StringAlignment.Near,
                StringAlignment.Center,
                ellipsis: false);

            DrawCheckMark(
                graphics,
                new Rectangle(textRight - S(13), bounds.Y + ((bounds.Height - S(13)) / 2), S(13), S(13)),
                PanelTheme.Amber);
        }
        else
        {
            DrawText(
                graphics,
                name,
                PanelTheme.Body,
                PanelTheme.Foreground2,
                new Rectangle(textX, bounds.Y, Math.Max(0, textRight - textX - S(74)), bounds.Height),
                StringAlignment.Near,
                StringAlignment.Center);

            DrawText(
                graphics,
                resolution,
                PanelTheme.MonoSmall,
                PanelTheme.Foreground3,
                new Rectangle(textRight - S(70), bounds.Y, S(70), bounds.Height),
                StringAlignment.Far,
                StringAlignment.Center,
                ellipsis: false);
        }
    }

    private void DrawCheckMark(Graphics graphics, Rectangle bounds, Color color)
    {
        using var pen = new Pen(color, PenWidth(1.4f));
        int x = bounds.X;
        int y = bounds.Y + S(6);
        graphics.DrawLine(pen, x + S(1), y, x + S(5), y + S(4));
        graphics.DrawLine(pen, x + S(5), y + S(4), x + S(12), y - S(4));
    }

    private void DrawTestView(Graphics graphics)
    {
        DrawTestConfig(graphics);
        DrawTestResult(graphics);
        DrawTestEnvironment(graphics);
    }

    private void DrawTestConfig(Graphics graphics)
    {
        Rectangle block = TestConfigRect;
        DrawBlock(graphics, block);
        DrawBlockHead(graphics, block, "测试配置", $"单次 {FormatDuration(_testDuration())} · 采样 1 秒");

        Rectangle row = TestDurationRow;
        DrawText(
            graphics,
            "单次时长",
            PanelTheme.Small,
            PanelTheme.Foreground2,
            new Rectangle(row.X, row.Y, S(70), row.Height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        int current = _testDuration();
        Rectangle[] chips = TestDurationChipRects();
        for (int index = 0; index < chips.Length; index++)
        {
            DrawChip(
                graphics,
                chips[index],
                TestDurationLabels[index],
                current == TestDurations[index],
                TestDurationActions[index]);
        }

        DrawText(
            graphics,
            "验收：丢帧 < 0.1% · 端到端 p95 ≤ 20 ms · max ≤ 35 ms · CPU ≤ 20% 单核",
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(block.X + S(12), row.Bottom + S(6), block.Width - S(24), S(16)),
            StringAlignment.Near,
            StringAlignment.Center);

        Rectangle button = TestStartButtonRect;
        bool hover = _hover == (_benchmarkRunning ? PanelAction.StopBenchmark : PanelAction.StartBenchmark);
        using (var path = Rounded(button, S(2)))
        {
            Color fill = _benchmarkRunning
                ? PanelTheme.Surface2
                : hover ? Color.FromArgb(255, 188, 85) : PanelTheme.Amber;
            using (var brush = new SolidBrush(fill))
            {
                graphics.FillPath(brush, path);
            }

            using var pen = new Pen(_benchmarkRunning ? PanelTheme.Error : PanelTheme.Line2, PenWidth(1));
            if (_benchmarkRunning || hover)
            {
                graphics.DrawPath(pen, path);
            }
        }

        DrawText(
            graphics,
            _benchmarkRunning ? "停止测试" : $"开始测试 · {FormatDuration(_testDuration())}",
            PanelTheme.BodyStrong,
            _benchmarkRunning ? PanelTheme.Error : PanelTheme.AmberInk,
            button,
            StringAlignment.Center,
            StringAlignment.Center,
            ellipsis: false);
    }

    private void DrawTestResult(Graphics graphics)
    {
        Rectangle block = TestResultRect;
        DrawBlock(graphics, block);

        string right = _benchmarkRunning
            ? "运行中 · 与 0.0.2 基线对照"
            : _lastBenchmark is { } last
                ? $"{last.StartedAt:MM-dd HH:mm} · {FormatDuration((int)last.Duration.TotalSeconds)}"
                : "尚未运行";
        DrawBlockHead(graphics, block, "上次结果", right);

        var body = new Rectangle(
            block.X + S(12),
            block.Y + S(BlockHeadHeight) + S(10),
            block.Width - S(24),
            block.Height - S(BlockHeadHeight) - S(20));

        if (_benchmarkRunning)
        {
            DrawTestProgress(graphics, body);
            return;
        }

        if (_lastBenchmark is not { } result)
        {
            DrawText(
                graphics,
                "尚未运行过基准测试。选择单次时长后点「开始测试」。",
                PanelTheme.Body,
                PanelTheme.Foreground3,
                body,
                StringAlignment.Center,
                StringAlignment.Center);
            return;
        }

        DrawTestTable(graphics, body, result);

        if (_lastReportPath is not null)
        {
            DrawText(
                graphics,
                $"报告：{_lastReportPath}",
                PanelTheme.MonoSmall,
                PanelTheme.Foreground3,
                new Rectangle(body.X, block.Bottom - S(36), body.Width - S(110), S(18)),
                StringAlignment.Near,
                StringAlignment.Center);
            DrawQuietButton(graphics, TestReportButtonRect, "打开日志目录", PanelAction.OpenLog);
        }
    }

    private void DrawTestProgress(Graphics graphics, Rectangle body)
    {
        int elapsed = (int)Math.Clamp(
            (DateTime.UtcNow - _benchmarkStartedAt).TotalSeconds,
            0,
            _benchmarkRunSeconds);

        var track = new Rectangle(body.X, body.Y + S(10), body.Width - S(120), S(6));
        using (var brush = new SolidBrush(PanelTheme.Line))
        {
            graphics.FillRectangle(brush, track);
        }

        int filled = (int)(track.Width * (elapsed / (double)Math.Max(1, _benchmarkRunSeconds)));
        if (filled > 0)
        {
            using var brush = new SolidBrush(PanelTheme.Amber);
            graphics.FillRectangle(brush, track.X, track.Y, filled, track.Height);
        }

        DrawText(
            graphics,
            $"{elapsed}s / {_benchmarkRunSeconds}s",
            PanelTheme.MonoSmall,
            PanelTheme.Foreground3,
            new Rectangle(track.Right + S(8), track.Y - S(6), S(110), S(18)),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        int metricY = track.Bottom + S(22);
        int column = body.Width / 3;
        DrawTestMetric(graphics, new Rectangle(body.X, metricY, column, S(38)), "捕获帧率", _live ? $"{_latest.CaptureFps:F1}" : "—", "fps");
        DrawTestMetric(graphics, new Rectangle(body.X + column, metricY, column, S(38)), "端到端 p95", _live ? $"{_latest.PresentP95:F1}" : "—", "ms");
        DrawTestMetric(graphics, new Rectangle(body.X + (column * 2), metricY, column, S(38)), "丢帧", _live ? _latest.Dropped.ToString(CultureInfo.InvariantCulture) : "—", null);

        DrawText(
            graphics,
            "测试期间可继续操作面板，完成后自动写入报告。",
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(body.X, metricY + S(46), body.Width, S(16)),
            StringAlignment.Near,
            StringAlignment.Center);
    }

    private void DrawTestMetric(
        Graphics graphics,
        Rectangle area,
        string label,
        string value,
        string? unit)
    {
        DrawText(
            graphics,
            label,
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(area.X, area.Y, area.Width, S(14)),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        var valueBounds = new Rectangle(area.X, area.Y + S(14), area.Width, area.Height - S(14));
        DrawText(
            graphics,
            value,
            PanelTheme.MonoValue,
            PanelTheme.Foreground,
            valueBounds,
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        if (unit is null)
        {
            return;
        }

        SizeF size = Measure(graphics, value, PanelTheme.MonoValue);
        DrawText(
            graphics,
            unit,
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(area.X + (int)Math.Ceiling(size.Width) + S(4), valueBounds.Y, area.Width, valueBounds.Height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);
    }

    private void DrawTestTable(Graphics graphics, Rectangle body, BenchmarkResult result)
    {
        int headerHeight = S(22);
        int rowHeight = S(24);

        DrawTestCell(graphics, "指标", PanelTheme.Small, PanelTheme.Foreground3, body, columnFrom: 0, columnTo: 1, 0, headerHeight, StringAlignment.Near);
        DrawTestCell(graphics, "本次", PanelTheme.Small, PanelTheme.Foreground3, body, 1, 2, 0, headerHeight, StringAlignment.Near);
        DrawTestCell(graphics, "基线 0.0.2", PanelTheme.Small, PanelTheme.Foreground3, body, 2, 3, 0, headerHeight, StringAlignment.Near);
        DrawTestCell(graphics, "判定", PanelTheme.Small, PanelTheme.Foreground3, body, 3, 4, 0, headerHeight, StringAlignment.Far);

        double dropRate = result.Captured == 0 ? 0 : result.Dropped / (double)result.Captured;
        bool p95Ok = result.Present.P95 <= 20;
        bool maxOk = result.Present.Max <= 35;
        bool dropOk = dropRate < 0.001;
        bool cpuOk = result.CpuPercentSingleCore <= 20;

        // 基线取第 8 节记录的 0.0.2 CPU 读回路径实测值
        (string Name, string Value, string Baseline, string Verdict, Color Color)[] rows =
        [
            ("捕获帧率", $"{result.CaptureFps:F1} fps", "—", "—", PanelTheme.Foreground3),
            ("呈现帧率", $"{result.PresentFps:F1} fps", "24–32 fps", "—", PanelTheme.Foreground3),
            ("回调年龄 p95", $"{result.Age.P95:F1} ms", "—", "—", PanelTheme.Foreground3),
            ("端到端 p95", $"{result.Present.P95:F1} ms", "39–57 ms", p95Ok ? "通过" : "未通过", p95Ok ? PanelTheme.Ok : PanelTheme.Error),
            ("端到端 max", $"{result.Present.Max:F1} ms", "70 ms", maxOk ? "通过" : "未通过", maxOk ? PanelTheme.Ok : PanelTheme.Error),
            ("丢帧率", $"{dropRate * 100:F2}%", "约一半", dropOk ? "通过" : "未通过", dropOk ? PanelTheme.Ok : PanelTheme.Error),
            ("CPU", $"{result.CpuPercentSingleCore:F1}% 单核", "约 100%", cpuOk ? "通过" : "未通过", cpuOk ? PanelTheme.Ok : PanelTheme.Error),
        ];

        for (int index = 0; index < rows.Length; index++)
        {
            int offset = headerHeight + (index * rowHeight);
            (string name, string value, string baseline, string verdict, Color color) = rows[index];

            DrawTestCell(graphics, name, PanelTheme.Small, PanelTheme.Foreground2, body, 0, 1, offset, rowHeight, StringAlignment.Near);
            DrawTestCell(graphics, value, PanelTheme.MonoSmall, PanelTheme.Foreground, body, 1, 2, offset, rowHeight, StringAlignment.Near);
            DrawTestCell(graphics, baseline, PanelTheme.MonoSmall, PanelTheme.Foreground3, body, 2, 3, offset, rowHeight, StringAlignment.Near);
            DrawTestCell(graphics, verdict, PanelTheme.Small, color, body, 3, 4, offset, rowHeight, StringAlignment.Far);
        }

        DrawLine(
            graphics,
            body.X,
            body.Y + headerHeight - 1,
            body.Right,
            body.Y + headerHeight - 1,
            PanelTheme.Line);
        int tableBottom = body.Y + headerHeight + (rows.Length * rowHeight);
        DrawLine(graphics, body.X, tableBottom, body.Right, tableBottom, PanelTheme.Line);

        DrawText(
            graphics,
            "判定基于第 8 节验收阈值；WGC 按需出帧，帧率不作固定目标。",
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(body.X, tableBottom + S(8), body.Width, S(16)),
            StringAlignment.Near,
            StringAlignment.Center);
    }

    private static void DrawTestCell(
        Graphics graphics,
        string text,
        Font font,
        Color color,
        Rectangle body,
        int columnFrom,
        int columnTo,
        int offset,
        int height,
        StringAlignment align)
    {
        float[] columns = [0f, 0.40f, 0.62f, 0.82f, 1f];
        int x = body.X + (int)(body.Width * columns[columnFrom]);
        int width = (int)(body.Width * (columns[columnTo] - columns[columnFrom]));

        DrawText(
            graphics,
            text,
            font,
            color,
            new Rectangle(x, body.Y + offset, width, height),
            align,
            StringAlignment.Center);
    }

    private void DrawTestEnvironment(Graphics graphics)
    {
        Rectangle block = TestEnvironmentRect;
        DrawBlock(graphics, block);
        DrawBlockHead(graphics, block, "测试环境", right: null);

        MonitorDescriptor source = _sourceMonitor();
        MonitorDescriptor preview = _previewMonitor();
        (int previewWidth, int previewHeight) = _previewSize();

        int bodyY = block.Y + S(BlockHeadHeight) + S(10);
        int column = (block.Width - S(24)) / 4;
        int x = block.X + S(12);

        DrawEnvironmentCell(graphics, new Rectangle(x, bodyY, column, S(40)), "适配器", _adapterName());
        DrawEnvironmentCell(
            graphics,
            new Rectangle(x + column, bodyY, column, S(40)),
            "源 → 预览",
            $"{source.Width}×{source.Height} → {preview.Width}×{preview.Height}");
        DrawEnvironmentCell(
            graphics,
            new Rectangle(x + (column * 2), bodyY, column, S(40)),
            "预览窗",
            $"{previewWidth}×{previewHeight}");
        DrawEnvironmentCell(
            graphics,
            new Rectangle(x + (column * 3), bodyY, column, S(40)),
            "版本",
            $"v{_version}");
    }

    private void DrawEnvironmentCell(Graphics graphics, Rectangle area, string label, string value)
    {
        DrawText(
            graphics,
            label,
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(area.X, area.Y, area.Width, S(14)),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        DrawText(
            graphics,
            value,
            PanelTheme.MonoSmall,
            PanelTheme.Foreground2,
            new Rectangle(area.X, area.Y + S(16), area.Width, area.Height - S(16)),
            StringAlignment.Near,
            StringAlignment.Center);
    }

    private void DrawDiagnosticsView(Graphics graphics)
    {
        DrawDiagnosticsDevice(graphics);
        DrawDiagnosticsLog(graphics);
    }

    private void DrawDiagnosticsDevice(Graphics graphics)
    {
        Rectangle block = DiagnosticsDeviceRect;
        DrawBlock(graphics, block);
        DrawBlockHead(graphics, block, "设备与管线", right: null);
        DrawQuietButton(graphics, DiagnosticsExportButtonRect, "导出诊断包", PanelAction.ExportDiagnostics);

        IReadOnlyList<DiagnosticEntry> entries = _diagnosticsSnapshot;
        int rows = (entries.Count + 1) / 2;
        int bodyTop = block.Y + S(BlockHeadHeight) + S(8);
        int rowHeight = S(24);
        int columnWidth = (block.Width - S(24)) / 2;
        int labelWidth = S(88);

        for (int row = 0; row < rows; row++)
        {
            DrawDiagnosticCell(
                graphics,
                entries[row],
                new Rectangle(block.X + S(12), bodyTop + (row * rowHeight), columnWidth, S(20)),
                labelWidth);

            int rightIndex = row + rows;
            if (rightIndex < entries.Count)
            {
                DrawDiagnosticCell(
                    graphics,
                    entries[rightIndex],
                    new Rectangle(
                        block.X + S(12) + columnWidth + S(12),
                        bodyTop + (row * rowHeight),
                        columnWidth,
                        S(20)),
                    labelWidth);
            }
        }

        if (_adapterFallback())
        {
            DrawText(
                graphics,
                "适配器已降级：高性能适配器不可用，呈现路径可能受影响",
                PanelTheme.Small,
                PanelTheme.Warn,
                new Rectangle(
                    block.X + S(12),
                    block.Bottom - S(30),
                    block.Width - S(24),
                    S(20)),
                StringAlignment.Near,
                StringAlignment.Center);
        }
    }

    private void DrawDiagnosticCell(
        Graphics graphics,
        DiagnosticEntry entry,
        Rectangle area,
        int labelWidth)
    {
        DrawText(
            graphics,
            entry.Label,
            PanelTheme.Small,
            PanelTheme.Foreground3,
            new Rectangle(area.X, area.Y, labelWidth, area.Height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        DrawText(
            graphics,
            entry.Value,
            PanelTheme.Small,
            PanelTheme.Foreground2,
            new Rectangle(area.X + labelWidth + S(6), area.Y, area.Width - labelWidth - S(6), area.Height),
            StringAlignment.Near,
            StringAlignment.Center);
    }

    private void DrawDiagnosticsLog(Graphics graphics)
    {
        Rectangle block = DiagnosticsLogRect;
        DrawBlock(graphics, block);
        DrawBlockHead(graphics, block, "最近日志", right: null);
        DrawQuietButton(graphics, DiagnosticsLogButtonRect, "日志目录", PanelAction.OpenLog);
        DrawQuietButton(
            graphics,
            DiagnosticsCopyButtonRect,
            "复制诊断摘要",
            PanelAction.CopyDiagnostics);

        int visible = DiagnosticsLogVisibleLines;
        int end = Math.Max(0, _logLines.Length - _logScroll);
        int start = Math.Max(0, end - visible);
        int y = DiagnosticsLogBodyTop;

        if (_logLines.Length == 0)
        {
            DrawText(
                graphics,
                "暂无日志。",
                PanelTheme.MonoSmall,
                PanelTheme.Foreground3,
                new Rectangle(block.X + S(12), DiagnosticsLogBodyTop, block.Width - S(24), S(20)),
                StringAlignment.Near,
                StringAlignment.Center);
        }

        for (int index = start; index < end; index++)
        {
            string line = _logLines[index];
            DrawText(
                graphics,
                line,
                PanelTheme.MonoSmall,
                LogLineColor(line),
                new Rectangle(
                    block.X + S(12),
                    y,
                    block.Width - S(24),
                    DiagnosticsLogLineHeight),
                StringAlignment.Near,
                StringAlignment.Center);
            y += DiagnosticsLogLineHeight;
        }

        bool showNotice = _diagnosticsNotice is not null
            && (DateTime.Now - _diagnosticsNoticeAt).TotalSeconds < 4;

        string footer = showNotice
            ? _diagnosticsNotice!
            : _logLines.Length > visible
                ? $"滚轮查看更早记录 · 已显示第 {start + 1}–{end} 行"
                : $"仅显示最近 {visible} 行";

        DrawText(
            graphics,
            footer,
            PanelTheme.MonoSmall,
            showNotice ? PanelTheme.Foreground2 : PanelTheme.Foreground3,
            new Rectangle(
                block.X + S(12),
                block.Bottom - S(32),
                block.Width - S(24) - S(124),
                S(20)),
            StringAlignment.Near,
            StringAlignment.Center);
    }

    private static Color LogLineColor(string line)
    {
        if (line.Contains("失败", StringComparison.Ordinal)
            || line.Contains("[致命]", StringComparison.Ordinal))
        {
            return PanelTheme.Error;
        }

        if (line.Contains("警告", StringComparison.Ordinal)
            || line.Contains("降级", StringComparison.Ordinal)
            || line.Contains("回退", StringComparison.Ordinal))
        {
            return PanelTheme.Warn;
        }

        return PanelTheme.Foreground2;
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

        DrawCornerTicks(graphics, thumb, PanelTheme.Line3);

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

        int versionWidth = S(48);
        int versionX = ClientSize.Width - S(14) - versionWidth;
        DrawText(
            graphics,
            $"v{_version}",
            PanelTheme.MonoSmall,
            PanelTheme.Foreground3,
            new Rectangle(versionX, y, versionWidth, height),
            StringAlignment.Far,
            StringAlignment.Center,
            ellipsis: false);

        int exitWidth = DrawShortcut(
            graphics, versionX - S(14), y, height, _exitShortcutKeys, "退出");
        int pauseWidth = DrawShortcut(
            graphics,
            versionX - S(14) - exitWidth - S(14),
            y,
            height,
            _pauseShortcutKeys,
            "暂停");

        // 日志路径只占快捷键左侧的剩余宽度，避免长路径压到按键提示上
        int logRight = versionX - S(14) - exitWidth - S(14) - pauseWidth - S(14);
        DrawText(
            graphics,
            $"日志 {_logFilePath}",
            PanelTheme.MonoSmall,
            PanelTheme.Foreground3,
            new Rectangle(S(14), y, Math.Max(0, logRight - S(14)), height),
            StringAlignment.Near,
            StringAlignment.Center);
    }

    /// <summary>绘制一组按键提示，返回整组占用的宽度，供右侧继续往左排布。</summary>
    private int DrawShortcut(
        Graphics graphics,
        int right,
        int y,
        int height,
        string[] keys,
        string label)
    {
        int gap = S(3);
        int padding = S(4);
        int keyHeight = S(16);
        int labelWidth = (int)Math.Ceiling(Measure(graphics, label, PanelTheme.MonoSmall).Width);

        var keyWidths = new int[keys.Length];
        int total = labelWidth + S(6);
        for (int index = 0; index < keys.Length; index++)
        {
            keyWidths[index] = (int)Math.Ceiling(Measure(graphics, keys[index], PanelTheme.Small).Width) + (padding * 2);
            total += keyWidths[index];
            if (index > 0)
            {
                total += gap;
            }
        }

        int x = right - total;
        int keyY = y + ((height - keyHeight) / 2);

        for (int index = 0; index < keys.Length; index++)
        {
            if (index > 0)
            {
                x += gap;
            }

            var key = new Rectangle(x, keyY, keyWidths[index], keyHeight);
            using (var path = Rounded(key, S(2)))
            {
                using var pen = new Pen(PanelTheme.Line2, PenWidth(1));
                graphics.DrawPath(pen, path);
            }

            DrawText(
                graphics,
                keys[index],
                PanelTheme.Small,
                PanelTheme.Foreground3,
                key,
                StringAlignment.Center,
                StringAlignment.Center,
                ellipsis: false);

            x += keyWidths[index];
        }

        x += S(6);
        DrawText(
            graphics,
            label,
            PanelTheme.MonoSmall,
            PanelTheme.Foreground3,
            new Rectangle(x, y, labelWidth + S(2), height),
            StringAlignment.Near,
            StringAlignment.Center,
            ellipsis: false);

        return total;
    }

    private void DrawCornerTicks(Graphics graphics, Rectangle rect, Color color)
    {
        int inset = S(3);
        int length = S(8);
        int left = rect.X + inset;
        int right = rect.Right - inset;
        int top = rect.Y + inset;
        int bottom = rect.Bottom - inset;

        using var pen = new Pen(color, PenWidth(1));
        graphics.DrawLine(pen, left, top, left + length, top);
        graphics.DrawLine(pen, left, top, left, top + length);
        graphics.DrawLine(pen, right - length, top, right, top);
        graphics.DrawLine(pen, right, top, right, top + length);
        graphics.DrawLine(pen, left, bottom - length, left, bottom);
        graphics.DrawLine(pen, left, bottom, left + length, bottom);
        graphics.DrawLine(pen, right, bottom - length, right, bottom);
        graphics.DrawLine(pen, right - length, bottom, right, bottom);
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
        bool disabled = _switching && action == PanelAction.SwapDirection;
        bool hover = !disabled && _hover == action;
        if (hover)
        {
            using var brush = new SolidBrush(PanelTheme.Surface2);
            graphics.FillRectangle(brush, rect);
        }

        using (var pen = new Pen(disabled ? PanelTheme.Line : PanelTheme.Line2, PenWidth(1)))
        {
            graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }

        DrawText(
            graphics,
            text,
            PanelTheme.Small,
            disabled ? PanelTheme.Line3 : hover ? PanelTheme.Foreground : PanelTheme.Foreground3,
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

    private Rectangle RailCollapseButtonRect => new(
        (S(RailWidth) - S(40)) / 2,
        BodyRect.Bottom - S(56),
        S(40),
        S(48));

    private Rectangle CollapsedExpandButtonRect => new(
        ClientSize.Width - S(12) - S(64),
        (ClientSize.Height - S(24)) / 2,
        S(64),
        S(24));

    private Rectangle CollapsedToggleRect => new(
        CollapsedExpandButtonRect.X - S(84),
        (ClientSize.Height - S(15)) / 2,
        S(28),
        S(15));

    private Rectangle OnboardDialogRect => new(
        (ClientSize.Width - S(720)) / 2,
        (ClientSize.Height - S(460)) / 2,
        S(720),
        S(460));

    private Rectangle OnboardSkipRect => new(
        OnboardDialogRect.X + S(20),
        OnboardDialogRect.Bottom - S(52),
        S(100),
        S(28));

    private Rectangle OnboardNextRect => new(
        OnboardDialogRect.Right - S(20) - S(140),
        OnboardDialogRect.Bottom - S(52),
        S(140),
        S(28));

    private Rectangle OnboardPrevRect => new(
        OnboardNextRect.X - S(12) - S(96),
        OnboardDialogRect.Bottom - S(52),
        S(96),
        S(28));

    private Rectangle OnboardSwapRect => new(
        OnboardDialogRect.Right - S(20) - S(92),
        OnboardDialogRect.Y + S(BlockHeadHeight) + S(6),
        S(92),
        S(24));

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _statusTimer.Dispose();
            _benchmarkTimer.Dispose();
            _animationTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
