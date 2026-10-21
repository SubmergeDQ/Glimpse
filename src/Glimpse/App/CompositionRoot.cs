using Glimpse.Abstractions.Display;
using Glimpse.Capture;
using Glimpse.Diagnostics;
using Glimpse.Display;
using Glimpse.Interop;
using Glimpse.Platform;
using Glimpse.Render;
using Glimpse.Ui;

namespace Glimpse.App;

/// <summary>
/// 组合根：全应用唯一的对象装配入口。
/// 除这里之外，任何层都不得直接创建其他层的具体实现，这是依赖方向不失控的关键约束。
/// </summary>
internal static class CompositionRoot
{
    private const int HotKeyToggleClickThrough = 1;
    private const int HotKeyExit = 2;
    private const uint VirtualKeyP = 0x50;
    private const uint VirtualKeyQ = 0x51;

    public static void Run(string[] args)
    {
        using SingleInstance instance = SingleInstance.Acquire();
        if (!instance.IsOwner)
        {
            MessageBox.Show(
                "Glimpse 已在运行。",
                "Glimpse",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        DiagnosticConsole.AttachToParent();

        using var log = new DiagnosticsLog();
        try
        {
            RunCore(log, StartupOptions.Parse(args));
        }
        catch (Exception ex)
        {
            log.WriteLine($"[致命] {ex}");
            MessageBox.Show(
                $"Glimpse 启动失败：{ex.Message}",
                "Glimpse",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void RunCore(DiagnosticsLog log, StartupOptions options)
    {
        log.WriteLine($"Glimpse 0.0.2 启动（{DateTime.Now:yyyy-MM-dd HH:mm:ss}），日志：{log.LogFilePath}");

        var clock = new FrameClock();
        var monitors = new MonitorEnumerator().Enumerate();
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("未检测到显示器。");
        }

        foreach (MonitorDescriptor monitor in monitors)
        {
            log.WriteLine(
                $"[显示器] #{monitor.Index} {(monitor.IsPrimary ? "主屏" : "副屏")} " +
                $"{monitor.Width}x{monitor.Height} @ ({monitor.X},{monitor.Y})");
        }

        MonitorSelection selection = options.Resolve(monitors, out string? selectionWarning);
        if (selectionWarning is not null)
        {
            log.WriteLine($"[启动] {selectionWarning}");
        }

        MonitorDescriptor sourceMonitor = selection.Source;
        MonitorDescriptor previewMonitor = selection.Preview;
        log.WriteLine($"[选择] 捕获 #{sourceMonitor.Index}，预览 #{previewMonitor.Index}");

        // 采集与呈现共用同一个 D3D 设备，纹理不会跨设备拷贝；
        // 即使两屏分属不同适配器，WGC 也会在给定设备上产出帧纹理。
        // 这里只做提示，便于混合显卡机器上出现异常时快速定位。
        Vortice.Luid? sourceAdapter = D3D11Interop.TryFindAdapterLuid(sourceMonitor.Handle);
        Vortice.Luid? previewAdapter = D3D11Interop.TryFindAdapterLuid(previewMonitor.Handle);
        if (sourceAdapter is { } sourceLuid
            && previewAdapter is { } previewLuid
            && (sourceLuid.LowPart != previewLuid.LowPart || sourceLuid.HighPart != previewLuid.HighPart))
        {
            log.WriteLine("[GPU] 提示：源屏与预览屏分属不同适配器，统一使用高性能适配器采集与呈现");
        }

        // 设备优先使用系统高性能适配器；按显示器选适配器在混合显卡机器上可能选到核显，
        // DirectComposition 内容无法合成到预览输出，表现为预览窗全黑。
        using GpuDevice gpu = D3D11Interop.CreateDevice(previewMonitor.Handle);
        log.WriteLine($"[GPU] {gpu.AdapterName}，特性级别 {gpu.FeatureLevel}");

        var stats = new FrameStats();
        var captureLatency = new LatencyMonitor();
        var presentLatency = new LatencyMonitor();
        var reporter = new StatsReporter(stats, captureLatency, presentLatency, log);

        WgcFrameSource? source = null;
        PreviewWindow? window = null;
        D3DPreviewHost? host = null;
        RenderLoop? loop = null;
        GlobalHotKey? clickThroughHotKey = null;
        GlobalHotKey? exitHotKey = null;

        try
        {
            source = new WgcFrameSource(gpu, sourceMonitor, clock, stats, captureLatency, log);

            window = new PreviewWindow();
            window.SetSourceSize(source.FrameSize.Width, source.FrameSize.Height);

            // 先让 WinForms 完成首次布局，再按物理像素覆盖位置与尺寸，
            // 避免 Show 时的默认布局覆盖 SetWindowPos 的结果。
            window.Show();

            IntPtr windowHandle = window.Handle;
            (int x, int y, int width, int height) = PreviewLayout.Compute(
                previewMonitor,
                source.FrameSize.Width,
                source.FrameSize.Height);
            WindowPlacement.Place(windowHandle, x, y, width, height);

            (int clientWidth, int clientHeight) = WindowPlacement.GetClientSize(windowHandle);
            host = new D3DPreviewHost(gpu, windowHandle, clientWidth, clientHeight, stats, log);
            window.AttachHost(host);

            if (WindowCaptureExclusion.Apply(windowHandle))
            {
                log.WriteLine("[窗口] 已从屏幕捕获中排除");
            }
            else
            {
                log.WriteLine("[窗口] 警告：系统不支持 WDA_EXCLUDEFROMCAPTURE，预览窗可能被拍进画面");
            }

            clickThroughHotKey = TryCreateHotKey(
                HotKeyToggleClickThrough,
                GlobalHotKey.ControlAlt,
                VirtualKeyP,
                "Ctrl+Alt+P 切换点击穿透",
                () => host.IsClickThrough = !host.IsClickThrough,
                log)
                ?? TryCreateHotKey(
                    HotKeyToggleClickThrough,
                    GlobalHotKey.ControlShift,
                    VirtualKeyP,
                    "Ctrl+Shift+P 切换点击穿透（备选）",
                    () => host.IsClickThrough = !host.IsClickThrough,
                    log);

            exitHotKey = TryCreateHotKey(
                HotKeyExit,
                GlobalHotKey.ControlAlt,
                VirtualKeyQ,
                "Ctrl+Alt+Q 退出",
                () => window.Close(),
                log)
                ?? TryCreateHotKey(
                    HotKeyExit,
                    GlobalHotKey.ControlShift,
                    VirtualKeyQ,
                    "Ctrl+Shift+Q 退出（备选）",
                    () => window.Close(),
                    log);

            loop = new RenderLoop(
                source,
                host,
                clock,
                presentLatency,
                reporter,
                log,
                () => !host.IsFaulted);

            try
            {
                source.StartCapture();
                loop.Start();
            }
            catch (Exception ex)
            {
                log.WriteLine($"[启动] 采集启动失败：{ex}");
                window.Close();
            }

            Application.Run(window);
        }
        finally
        {
            // 关闭顺序必须与创建相反：先停渲染，再停采集，最后释放呈现与设备
            loop?.Dispose();
            source?.Dispose();
            host?.Dispose();
            clickThroughHotKey?.Dispose();
            exitHotKey?.Dispose();
        }

        reporter.ReportFinal();
        log.WriteLine("Glimpse 已退出");
    }

    private static GlobalHotKey? TryCreateHotKey(
        int id,
        uint modifiers,
        uint virtualKey,
        string description,
        Action action,
        DiagnosticsLog log)
    {
        try
        {
            var hotKey = new GlobalHotKey(id, modifiers, virtualKey);
            hotKey.Pressed += action;
            log.WriteLine($"[热键] {description} 已注册");
            return hotKey;
        }
        catch (Exception ex)
        {
            log.WriteLine($"[热键] {description} 注册失败：{ex.Message}");
            return null;
        }
    }
}
