using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Glimpse.Interop;

namespace Glimpse.Platform;

/// <summary>托盘图标状态。所有状态共用同一形状，只换颜色。</summary>
internal enum TrayIconState
{
    Normal,
    ClickThrough,
    Paused,
}

/// <summary>托盘里的一个捕获源选项。</summary>
internal sealed record TrayMonitorItem(string Label, bool Checked, Action Select);

/// <summary>
/// 托盘入口。菜单项与全局热键调用同一组回调，避免两套状态各自漂移。
/// 图标在运行时自绘，不依赖外部资源文件。
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private static readonly Color NormalColor = Color.FromArgb(232, 235, 240);
    private static readonly Color ClickThroughColor = Color.FromArgb(242, 169, 59);
    private static readonly Color PausedColor = Color.FromArgb(138, 145, 156);

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _sourceItem;
    private readonly ToolStripMenuItem _clickThroughItem;
    private readonly ToolStripMenuItem _previewItem;
    private readonly ToolStripMenuItem _overlayItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _benchmarkItem;
    private readonly Func<bool> _isPreviewVisible;
    private readonly Func<bool> _isOverlayVisible;
    private readonly Func<bool> _isPaused;
    private readonly Func<IReadOnlyList<TrayMonitorItem>> _monitors;
    private readonly Dictionary<TrayIconState, IconHandle> _icons = new();
    private bool _disposed;

    public TrayIconHost(
        string logDirectory,
        string statusText,
        Action toggleClickThrough,
        Func<bool> isPreviewVisible,
        Func<bool> isOverlayVisible,
        Func<bool> isPaused,
        Action togglePreview,
        Action toggleOverlay,
        Action togglePause,
        Action openPanel,
        Action exit,
        Action runBenchmark,
        Func<IReadOnlyList<TrayMonitorItem>> monitors,
        bool clickThroughAvailable = true)
    {
        _icons[TrayIconState.Normal] = CreateIcon(NormalColor);
        _icons[TrayIconState.ClickThrough] = CreateIcon(ClickThroughColor);
        _icons[TrayIconState.Paused] = CreateIcon(PausedColor);

        _statusItem = new ToolStripMenuItem(statusText) { Enabled = false };
        _monitors = monitors;
        _sourceItem = new ToolStripMenuItem("捕获源");
        _sourceItem.DropDownOpening += (_, _) => RebuildSourceMenu();

        _clickThroughItem = new ToolStripMenuItem(
            clickThroughAvailable ? "点击穿透" : "点击穿透（暂不可用）")
        {
            Enabled = clickThroughAvailable,
        };
        _clickThroughItem.Click += (_, _) => toggleClickThrough();

        _isOverlayVisible = isOverlayVisible;
        _overlayItem = new ToolStripMenuItem("窗口内读数");
        _overlayItem.Click += (_, _) =>
        {
            toggleOverlay();
            RefreshOverlayItem();
        };

        // 预览窗没有边框与关闭按钮，显示与隐藏都只能从托盘触发；
        // 菜单项文本按窗口当前可见性切换，避免同时出现两个互斥动作
        _isPreviewVisible = isPreviewVisible;
        _previewItem = new ToolStripMenuItem("隐藏预览");
        _previewItem.Click += (_, _) =>
        {
            togglePreview();
            RefreshPreviewItem();
        };

        _isPaused = isPaused;
        _pauseItem = new ToolStripMenuItem("暂停监看");
        _pauseItem.Click += (_, _) =>
        {
            togglePause();
            RefreshPauseItem();
        };

        var panelItem = new ToolStripMenuItem("打开面板");
        panelItem.Click += (_, _) => openPanel();

        var logItem = new ToolStripMenuItem("打开日志目录");
        logItem.Click += (_, _) => OpenDirectory(logDirectory);

        _benchmarkItem = new ToolStripMenuItem("基准测试（60 秒）");
        _benchmarkItem.Click += (_, _) => runBenchmark();

        var exitItem = new ToolStripMenuItem("退出 Glimpse");
        exitItem.Click += (_, _) => exit();

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(panelItem);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(_sourceItem);
        _menu.Items.Add(_previewItem);
        _menu.Items.Add(_overlayItem);
        _menu.Items.Add(_clickThroughItem);
        _menu.Items.Add(_benchmarkItem);
        _menu.Items.Add(logItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        // 窗口可能被其他途径隐藏，每次展开菜单都按真实可见性刷新
        _menu.Opening += (_, _) =>
        {
            RefreshPreviewItem();
            RefreshOverlayItem();
            RefreshPauseItem();
        };

        _notifyIcon = new NotifyIcon
        {
            Icon = _icons[TrayIconState.Normal].Icon,
            Text = "Glimpse",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        // 双击托盘打开主控面板；面板隐藏时也走同一入口
        _notifyIcon.DoubleClick += (_, _) => openPanel();
    }

    public void SetClickThrough(bool enabled)
    {
        _clickThroughItem.Checked = enabled;
        _notifyIcon.Text = enabled ? "Glimpse · 点击穿透中" : "Glimpse";
        _notifyIcon.Icon = _icons[enabled ? TrayIconState.ClickThrough : TrayIconState.Normal].Icon;
    }

    private void RefreshPreviewItem() =>
        _previewItem.Text = _isPreviewVisible() ? "隐藏预览" : "显示预览";

    private void RefreshOverlayItem() => _overlayItem.Checked = _isOverlayVisible();

    private void RefreshPauseItem() =>
        _pauseItem.Text = _isPaused() ? "继续监看" : "暂停监看";

    /// <summary>暂停状态同时反映到图标颜色与悬浮文本。</summary>
    public void SetPaused(bool paused)
    {
        _notifyIcon.Icon = _icons[paused ? TrayIconState.Paused : TrayIconState.Normal].Icon;
        _notifyIcon.Text = paused ? "Glimpse · 已暂停" : "Glimpse";
        RefreshPauseItem();
    }

    /// <summary>每次展开时重建，保证勾选状态与当前捕获会话一致。</summary>
    private void RebuildSourceMenu()
    {
        _sourceItem.DropDownItems.Clear();

        foreach (TrayMonitorItem monitor in _monitors())
        {
            var item = new ToolStripMenuItem(monitor.Label) { Checked = monitor.Checked };
            item.Click += (_, _) => monitor.Select();
            _sourceItem.DropDownItems.Add(item);
        }
    }

    public void SetStatusText(string text) => _statusItem.Text = text;

    public void SetBenchmarkEnabled(bool enabled) => _benchmarkItem.Enabled = enabled;

    internal static void OpenDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
    }

    /// <summary>
    /// 32×32 双屏轮廓：两个错位的方框加一条连接线，对应主副屏拓扑。
    /// 线宽 2.2 像素，缩到托盘 16 像素时约 1.1 像素，接近设计稿的描边要求。
    /// </summary>
    private static IconHandle CreateIcon(Color color)
    {
        const int size = 32;

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var pen = new Pen(color, 2.2f)
            {
                StartCap = LineCap.Square,
                EndCap = LineCap.Square,
                LineJoin = LineJoin.Miter,
            };

            graphics.DrawRectangle(pen, 3, 6, 13, 11);
            graphics.DrawRectangle(pen, 17, 15, 12, 11);
            graphics.DrawLine(pen, 16, 12, 17, 20);
        }

        // Icon.FromHandle 不接管 HICON 的所有权，句柄必须在 Dispose 时单独释放
        IntPtr handle = bitmap.GetHicon();
        return new IconHandle(Icon.FromHandle(handle), handle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();

        foreach (IconHandle icon in _icons.Values)
        {
            icon.Icon.Dispose();
            WindowInterop.DestroyIcon(icon.Handle);
        }

        _icons.Clear();
    }

    private sealed record IconHandle(Icon Icon, IntPtr Handle);
}
