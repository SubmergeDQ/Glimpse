using Glimpse.Abstractions.Configuration;
using Glimpse.Abstractions.Display;
using Glimpse.Capture;
using Glimpse.Configuration;
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
    private const int HotKeyExit = 2;
    private const uint VirtualKeyQ = 0x51;

    public static void Run(string[] args)
    {
        using SingleInstance instance = SingleInstance.Acquire();
        if (!instance.IsOwner)
        {
            // 不弹窗、不新起进程：请已有实例显示并置前预览窗
            SingleInstance.RequestActivation();
            return;
        }

        DiagnosticConsole.AttachToParent();

        using var log = new DiagnosticsLog();
        try
        {
            RunCore(log, StartupOptions.Parse(args), instance);
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

    private static void RunCore(DiagnosticsLog log, StartupOptions options, SingleInstance instance)
    {
        string version = typeof(CompositionRoot).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        string logDirectory = Path.GetDirectoryName(log.LogFilePath) ?? string.Empty;
        log.WriteLine($"Glimpse {version} 启动（{DateTime.Now:yyyy-MM-dd HH:mm:ss}），日志：{log.LogFilePath}");

        SettingsStore settingsStore = SettingsStore.CreateDefault();
        AppSettings settings = settingsStore.Load(out string? settingsWarning);
        if (settingsWarning is not null)
        {
            log.WriteLine($"[设置] {settingsWarning}");
        }
        else if (settings.Placement is { } savedPlacement)
        {
            log.WriteLine(
                $"[设置] 已读取保存位置 ({savedPlacement.X},{savedPlacement.Y}) " +
                $"{savedPlacement.Width}x{savedPlacement.Height}");
        }

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
                $"{monitor.Width}x{monitor.Height} @ ({monitor.X},{monitor.Y}) {monitor.DeviceName}");
        }

        MonitorSelection selection = options.Resolve(monitors, settings, out string? selectionWarning);
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
        var stats = new FrameStats();
        var captureLatency = new LatencyMonitor();
        var presentLatency = new LatencyMonitor();
        var liveStats = new LiveStatsChannel();
        var reporter = new StatsReporter(stats, captureLatency, presentLatency, log, liveStats);
        var benchmark = new BenchmarkRunner(stats, captureLatency, presentLatency);

        GpuDevice? gpu = null;
        CaptureSession? session = null;
        PreviewWindow? window = null;
        MainPanelWindow? panel = null;
        StatsOverlayWindow? overlay = null;
        D3DPreviewHost? host = null;
        RenderLoop? loop = null;
        GlobalHotKey? exitHotKey = null;
        TrayIconHost? tray = null;
        System.Windows.Forms.Timer? healthTimer = null;
        System.Windows.Forms.Timer? displayDebounce = null;
        bool exiting = false;
        bool recovering = false;
        bool captureExcluded = false;
        int recoverAttempts = 0;
        int previewScale = Math.Clamp(settings.PreviewScalePercent, 1, 100);
        PreviewAnchor previewAnchor = settings.PreviewAnchor;
        int testDuration = settings.TestDurationSeconds is 15 or 60 or 300
            ? settings.TestDurationSeconds
            : 60;

        try
        {
            gpu = D3D11Interop.CreateDevice(previewMonitor.Handle);
            log.WriteLine($"[GPU] {gpu.AdapterName}，特性级别 {gpu.FeatureLevel}");

            session = new CaptureSession(gpu, sourceMonitor, clock, stats, captureLatency, log);
            WgcFrameSource source = session.Source;

            window = new PreviewWindow();
            PreviewWindow previewWindow = window;
            previewWindow.SetSourceSize(source.FrameSize.Width, source.FrameSize.Height);

            // 先让 WinForms 完成首次布局，再按物理像素覆盖位置与尺寸，
            // 避免 Show 时的默认布局覆盖 SetWindowPos 的结果。
            previewWindow.Show();

            IntPtr windowHandle = previewWindow.Handle;
            (int x, int y, int width, int height) = ResolvePreviewBounds(
                settings,
                previewMonitor,
                source.FrameSize,
                monitors);
            WindowPlacement.Place(windowHandle, x, y, width, height);

            // WinForms 的 TopMost 属性有时不会落到 WS_EX_TOPMOST 上，这里显式置顶一次
            WindowPlacement.BringToFront(windowHandle);
            log.WriteLine(
                $"[窗口] 位置 ({x},{y}) {width}x{height}，置顶={WindowPlacement.IsTopMost(windowHandle)}");

            (int clientWidth, int clientHeight) = WindowPlacement.GetClientSize(windowHandle);
            host = new D3DPreviewHost(gpu, windowHandle, clientWidth, clientHeight, stats, log);
            previewWindow.AttachHost(host);

            if (settings.ClickThrough)
            {
                log.WriteLine("[窗口] 忽略已保存的点击穿透：当前呈现方式不支持穿透");
            }

            D3DPreviewHost previewHost = host;

            overlay = new StatsOverlayWindow(liveStats);
            StatsOverlayWindow statsOverlay = overlay;
            statsOverlay.Owner = previewWindow;
            bool overlayEnabled = settings.ShowLatencyOverlay;

            void PositionOverlay()
            {
                (int previewX, int previewY, _, _) = WindowPlacement.GetBounds(previewWindow.Handle);

                // 边距按读数框所在屏的 DPI 换算，避免高缩放屏上贴得过紧
                int margin = DpiScaling.Scale(8, WindowPlacement.GetDpi(statsOverlay.Handle));
                Size overlaySize = statsOverlay.ClientSize;
                WindowPlacement.Place(
                    statsOverlay.Handle,
                    previewX + margin,
                    previewY + margin,
                    overlaySize.Width,
                    overlaySize.Height);
            }

            void ShowOverlay()
            {
                statsOverlay.Show();
                PositionOverlay();
                WindowCaptureExclusion.Apply(statsOverlay.Handle);

                // 面板正在操作时保持在最上层，避免读数框压住面板
                if (panel is { Visible: true })
                {
                    WindowPlacement.SetTopMost(panel.Handle, true);
                }
            }

            void SetPreviewVisible(bool visible)
            {
                if (visible)
                {
                    previewWindow.Show();

                    // 重新显示时再显式置顶，避免 TopMost 属性未落到原生样式
                    WindowPlacement.BringToFront(previewWindow.Handle);
                    if (panel is { Visible: true })
                    {
                        WindowPlacement.SetTopMost(panel.Handle, true);
                    }

                    if (overlayEnabled)
                    {
                        ShowOverlay();
                    }
                }
                else
                {
                    previewWindow.Hide();
                    statsOverlay.Hide();
                }
            }

            void TogglePreview() => SetPreviewVisible(!previewWindow.Visible);

            void ToggleOverlay()
            {
                overlayEnabled = !overlayEnabled;

                if (overlayEnabled && previewWindow.Visible)
                {
                    ShowOverlay();
                }
                else
                {
                    statsOverlay.Hide();
                }
            }

            // 读数跟随预览窗移动与缩放，永远贴在客户区左上角；
            // 跨屏 DPI 变化时读数框自身尺寸会变，也要重新贴边
            previewWindow.LocationChanged += (_, _) => PositionOverlay();
            previewWindow.ClientSizeChanged += (_, _) => PositionOverlay();
            statsOverlay.SizeChanged += (_, _) => PositionOverlay();

            // 手动拖动或缩放后，贴边档位不再成立，自动切到「自由」
            previewWindow.UserMoved += () =>
            {
                if (previewAnchor == PreviewAnchor.Free)
                {
                    return;
                }

                previewAnchor = PreviewAnchor.Free;
                settings = settings with { PreviewAnchor = previewAnchor };
                log.WriteLine("[输出] 用户手动移动预览窗，贴边方式改为自由");
                panel?.RefreshNow();
            };

            if (overlayEnabled)
            {
                ShowOverlay();
            }

            void ToggleClickThrough()
            {
                bool enabled = !previewHost.IsClickThrough;
                previewHost.IsClickThrough = enabled;
                tray?.SetClickThrough(enabled);
            }

            void RequestExit()
            {
                DialogResult confirm = MessageBox.Show(
                    "退出后舞台会立刻失去预览，确定退出？",
                    "Glimpse",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (confirm != DialogResult.Yes)
                {
                    return;
                }

                exiting = true;
                previewWindow.Close();
            }

            previewWindow.FormClosing += (_, e) =>
            {
                SaveSettings(
                    settingsStore,
                    settings,
                    previewWindow,
                    sourceMonitor,
                    previewMonitor,
                    previewHost,
                    overlayEnabled,
                    log);

                // 关闭窗口只收进托盘，捕获与呈现继续运行；托盘「退出」才真正结束进程
                if (!exiting)
                {
                    e.Cancel = true;
                    previewWindow.Hide();
                }
            };

            async void RunBenchmark(TimeSpan duration, bool silent)
            {
                if (benchmark.IsRunning)
                {
                    return;
                }

                try
                {
                    tray?.SetBenchmarkEnabled(false);
                    log.WriteLine($"[基准] 开始 {duration.TotalSeconds:0} 秒基准测试");

                    BenchmarkResult result = await benchmark.RunAsync(duration);
                    string path = BenchmarkReport.Write(result, logDirectory);

                    log.WriteLine(
                        $"[基准] 捕获 {result.CaptureFps:F1} fps，呈现 {result.PresentFps:F1} fps，" +
                        $"丢弃 {result.Dropped}，present p95 {result.Present.P95:F1} ms，" +
                        $"CPU {result.CpuPercentSingleCore:F1}% 单核，报告：{path}");

                    if (silent)
                    {
                        exiting = true;
                        previewWindow.Close();
                        return;
                    }

                    MessageBox.Show(
                        $"基准测试完成\n\n" +
                        $"捕获 {result.CaptureFps:F1} fps / 呈现 {result.PresentFps:F1} fps\n" +
                        $"端到端 p95 {result.Present.P95:F1} ms\n" +
                        $"CPU {result.CpuPercentSingleCore:F1}% 单核\n\n" +
                        $"报告：{path}",
                        "Glimpse",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    log.WriteLine($"[基准] 失败：{ex}");

                    if (silent)
                    {
                        exiting = true;
                        previewWindow.Close();
                    }
                }
                finally
                {
                    tray?.SetBenchmarkEnabled(true);
                }
            }

            void StartPanelBenchmark(int seconds, Action<BenchmarkResult?, string?> completed)
            {
                if (benchmark.IsRunning)
                {
                    completed(null, null);
                    return;
                }

                async void Execute()
                {
                    try
                    {
                        tray?.SetBenchmarkEnabled(false);
                        log.WriteLine($"[基准] 面板开始 {seconds} 秒基准测试");

                        BenchmarkResult result = await benchmark.RunAsync(TimeSpan.FromSeconds(seconds));
                        string path = BenchmarkReport.Write(result, logDirectory);
                        log.WriteLine(
                            $"[基准] 捕获 {result.CaptureFps:F1} fps，呈现 {result.PresentFps:F1} fps，" +
                            $"丢弃 {result.Dropped}，present p95 {result.Present.P95:F1} ms，" +
                            $"CPU {result.CpuPercentSingleCore:F1}% 单核，报告：{path}");

                        completed(result, path);
                    }
                    catch (Exception ex)
                    {
                        log.WriteLine($"[基准] 面板运行失败：{ex}");
                        completed(null, null);
                    }
                    finally
                    {
                        tray?.SetBenchmarkEnabled(true);
                    }
                }

                Execute();
            }

            void OpenPanel()
            {
                if (panel is null || panel.IsDisposed)
                {
                    panel = new MainPanelWindow(
                        version,
                        log.LogFilePath ?? string.Empty,
                        liveStats,
                        () => sourceMonitor,
                        () => previewMonitor,
                        () => monitors,
                        SelectSource,
                        SelectPreview,
                        SwapDirection,
                        () => previewWindow.Visible,
                        TogglePreview,
                        () => overlayEnabled,
                        ToggleOverlay,
                        () => previewScale,
                        ApplyPreviewScale,
                        () => previewAnchor,
                        ApplyPreviewAnchor,
                        StartPanelBenchmark,
                        () => testDuration,
                        SetTestDuration,
                        () => gpu?.AdapterName ?? "—",
                        () => captureExcluded,
                        () => WindowPlacement.GetClientSize(previewWindow.Handle),
                        () => TrayIconHost.OpenDirectory(logDirectory),
                        () => RunBenchmark(TimeSpan.FromSeconds(60), silent: false));
                }

                panel.Show();
                if (panel.WindowState == FormWindowState.Minimized)
                {
                    panel.WindowState = FormWindowState.Normal;
                }

                // 预览窗常驻置顶，面板自身也临时置顶，激活后位于预览窗之上
                WindowPlacement.SetTopMost(panel.Handle, true);
                panel.Activate();
            }

            loop = new RenderLoop(
                session.Source,
                host,
                clock,
                presentLatency,
                reporter,
                log,
                () => !host.IsFaulted);

            CaptureSession activeSession = session;
            RenderLoop activeLoop = loop;

            // 第二个实例的激活请求要打开面板，注册放在会话就绪之后，避免调用尚未初始化的本地函数
            instance.ListenForActivation(() =>
            {
                if (previewWindow.IsDisposed)
                {
                    return;
                }

                // 事件在后台线程触发，切回 UI 线程再操作窗口
                previewWindow.BeginInvoke(() =>
                {
                    OpenPanel();
                    log.WriteLine("[单实例] 已响应第二个实例的激活请求");
                });
            });

            bool SwitchSource(MonitorDescriptor monitor)
            {
                if (!activeSession.TrySwitch(monitor, out string? switchError))
                {
                    if (switchError is not null)
                    {
                        log.WriteLine($"[切换] 失败，保留原会话：{switchError}");
                        MessageBox.Show(
                            $"切换到显示器 #{monitor.Index} 失败：{switchError}",
                            "Glimpse",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        return false;
                    }

                    // 目标就是当前捕获源，无需切换
                    return true;
                }

                // 先让渲染线程取新源，再调整窗口；旧源由 CaptureSession 延迟释放
                activeLoop.SwitchSource(activeSession.Source);

                // 退出时保存的是最后使用的捕获源
                sourceMonitor = monitor;

                (int sourceWidth, int sourceHeight) = activeSession.Source.FrameSize;
                previewWindow.SetSourceSize(sourceWidth, sourceHeight);

                (int currentX, int currentY, int currentWidth, int currentHeight) =
                    WindowPlacement.GetBounds(previewWindow.Handle);
                (int nextX, int nextY, int nextWidth, int nextHeight) = PreviewLayout.FitToMonitor(
                    currentX,
                    currentY,
                    currentWidth,
                    currentHeight,
                    sourceWidth,
                    sourceHeight,
                    previewMonitor);

                // 不改变 Z 序：面板打开时预览窗不应被抬到面板之上
                WindowPlacement.Move(previewWindow.Handle, nextX, nextY, nextWidth, nextHeight);

                if (previewAnchor != PreviewAnchor.Free)
                {
                    (int anchorX, int anchorY) = PreviewLayout.AnchorPosition(
                        previewMonitor,
                        nextWidth,
                        nextHeight,
                        previewAnchor);
                    WindowPlacement.Move(previewWindow.Handle, anchorX, anchorY, nextWidth, nextHeight);
                }

                tray?.SetStatusText($"捕获 #{monitor.Index} · 预览 #{previewMonitor.Index}");
                log.WriteLine($"[切换] 已切换到显示器 #{monitor.Index}，窗口 {nextWidth}x{nextHeight}");
                return true;
            }

            void SwitchPreview(MonitorDescriptor monitor)
            {
                if (monitor.Index == previewMonitor.Index)
                {
                    return;
                }

                MonitorDescriptor previous = previewMonitor;
                previewMonitor = monitor;

                (int x, int y, int width, int height) = WindowPlacement.GetBounds(previewWindow.Handle);
                (int sourceWidth, int sourceHeight) = activeSession.Source.FrameSize;
                (int nextX, int nextY, int nextWidth, int nextHeight) = PreviewLayout.FitToMonitor(
                    x,
                    y,
                    width,
                    height,
                    sourceWidth,
                    sourceHeight,
                    previewMonitor);
                WindowPlacement.Move(previewWindow.Handle, nextX, nextY, nextWidth, nextHeight);

                if (previewAnchor != PreviewAnchor.Free)
                {
                    (int anchorX, int anchorY) = PreviewLayout.AnchorPosition(
                        previewMonitor,
                        nextWidth,
                        nextHeight,
                        previewAnchor);
                    WindowPlacement.Move(previewWindow.Handle, anchorX, anchorY, nextWidth, nextHeight);
                }

                tray?.SetStatusText($"捕获 #{sourceMonitor.Index} · 预览 #{previewMonitor.Index}");
                log.WriteLine(
                    $"[输出] 预览已移到显示器 #{monitor.Index}（{monitor.DeviceName}），" +
                    $"窗口 {nextWidth}x{nextHeight}，原预览 #{previous.Index}");
            }

            void ApplyPreviewScale(int percent)
            {
                previewScale = Math.Clamp(percent, 1, 100);
                settings = settings with { PreviewScalePercent = previewScale };

                (int sourceWidth, int sourceHeight) = activeSession.Source.FrameSize;
                (int fitWidth, _) = PreviewLayout.FitSize(previewMonitor, sourceWidth, sourceHeight);
                int width = Math.Max(160, (int)Math.Round(fitWidth * previewScale / 100.0));
                int height = Math.Max(90, (int)Math.Round(width * sourceHeight / (double)sourceWidth));

                (int currentX, int currentY, int currentWidth, int currentHeight) =
                    WindowPlacement.GetBounds(previewWindow.Handle);
                int centerX = currentX + (currentWidth / 2);
                int centerY = currentY + (currentHeight / 2);
                int x = Math.Clamp(
                    centerX - (width / 2),
                    previewMonitor.X,
                    Math.Max(previewMonitor.X, previewMonitor.X + previewMonitor.Width - width));
                int y = Math.Clamp(
                    centerY - (height / 2),
                    previewMonitor.Y,
                    Math.Max(previewMonitor.Y, previewMonitor.Y + previewMonitor.Height - height));

                WindowPlacement.Move(previewWindow.Handle, x, y, width, height);
                log.WriteLine($"[输出] 预览尺寸档位 {previewScale}%，窗口 {width}x{height}");
                panel?.RefreshNow();
            }

            void SetTestDuration(int seconds)
            {
                testDuration = seconds is 15 or 60 or 300 ? seconds : 60;
                settings = settings with { TestDurationSeconds = testDuration };
                log.WriteLine($"[基准] 单次时长设为 {testDuration} 秒");
                panel?.RefreshNow();
            }

            void ApplyPreviewAnchor(PreviewAnchor anchor)
            {
                previewAnchor = anchor;
                settings = settings with { PreviewAnchor = previewAnchor };

                if (anchor == PreviewAnchor.Free)
                {
                    log.WriteLine("[输出] 贴边方式改为自由");
                    panel?.RefreshNow();
                    return;
                }

                (int _, int _, int width, int height) = WindowPlacement.GetBounds(previewWindow.Handle);
                (int x, int y) = PreviewLayout.AnchorPosition(previewMonitor, width, height, anchor);
                WindowPlacement.Move(previewWindow.Handle, x, y, width, height);
                log.WriteLine($"[输出] 预览贴边方式 {anchor}，窗口 {width}x{height} @ ({x},{y})");
                panel?.RefreshNow();
            }

            // 同一块屏不能同时做源和输出：选择源时若命中当前预览屏，预览自动换到原来源
            void SelectSource(MonitorDescriptor monitor)
            {
                MonitorDescriptor previousSource = sourceMonitor;
                if (!SwitchSource(monitor))
                {
                    return;
                }

                if (monitor.Index == previewMonitor.Index)
                {
                    SwitchPreview(previousSource);
                }
            }

            // 选择预览屏时若命中当前来源，先把捕获换到原预览屏，再把窗口移过去
            void SelectPreview(MonitorDescriptor monitor)
            {
                MonitorDescriptor previousPreview = previewMonitor;

                if (monitor.Index == sourceMonitor.Index)
                {
                    if (!SwitchSource(previousPreview))
                    {
                        return;
                    }
                }

                SwitchPreview(monitor);
            }

            void SwapDirection()
            {
                MonitorDescriptor previousSource = sourceMonitor;
                MonitorDescriptor previousPreview = previewMonitor;

                if (!SwitchSource(previousPreview))
                {
                    return;
                }

                SwitchPreview(previousSource);
            }

            // 设备丢失后由健康检查触发：停掉整条链路，重建设备、采集会话与呈现器
            void RecoverPipeline()
            {
                if (recovering || exiting)
                {
                    return;
                }

                recovering = true;
                recoverAttempts++;

                try
                {
                    log.WriteLine($"[恢复] 检测到设备丢失，重建 D3D 栈（第 {recoverAttempts} 次尝试）");

                    loop?.Dispose();
                    loop = null;

                    session?.Dispose();
                    session = null;

                    host?.Dispose();
                    host = null;

                    gpu?.Dispose();
                    gpu = null;

                    gpu = D3D11Interop.CreateDevice(previewMonitor.Handle);

                    activeSession = new CaptureSession(gpu, sourceMonitor, clock, stats, captureLatency, log);
                    session = activeSession;

                    (int clientWidth, int clientHeight) =
                        WindowPlacement.GetClientSize(previewWindow.Handle);
                    previewHost = new D3DPreviewHost(
                        gpu,
                        previewWindow.Handle,
                        clientWidth,
                        clientHeight,
                        stats,
                        log);
                    host = previewHost;
                    previewWindow.AttachHost(previewHost);
                    previewWindow.SetSourceSize(
                        activeSession.Source.FrameSize.Width,
                        activeSession.Source.FrameSize.Height);

                    activeLoop = new RenderLoop(
                        activeSession.Source,
                        previewHost,
                        clock,
                        presentLatency,
                        reporter,
                        log,
                        () => !previewHost.IsFaulted);
                    loop = activeLoop;

                    activeSession.Start();
                    activeLoop.Start();

                    recoverAttempts = 0;
                    if (healthTimer is not null)
                    {
                        healthTimer.Interval = 1000;
                    }

                    log.WriteLine("[恢复] D3D 栈重建完成");
                }
                catch (Exception ex)
                {
                    log.WriteLine($"[恢复] 重建失败：{ex}");

                    if (healthTimer is not null)
                    {
                        // 设备持续不可用时退避重试，避免每秒重建
                        healthTimer.Interval = Math.Clamp(recoverAttempts * 2, 2, 30) * 1000;
                    }
                }
                finally
                {
                    recovering = false;
                }
            }

            // 显示配置变化：重新枚举并按设备名匹配，源丢失时切到主屏
            void HandleDisplayChange()
            {
                IReadOnlyList<MonitorDescriptor> current = new MonitorEnumerator().Enumerate();
                if (current.Count == 0)
                {
                    log.WriteLine("[热插拔] 重新枚举后没有可用显示器");
                    return;
                }

                monitors = current;

                MonitorDescriptor? source = current.FirstOrDefault(monitor =>
                    monitor.DeviceName == sourceMonitor.DeviceName
                    && monitor.Width == sourceMonitor.Width
                    && monitor.Height == sourceMonitor.Height);

                if (source is null)
                {
                    // 设备名被移除或重新分配给其他显示器：停止捕获、保留最后一帧，等用户选源
                    if (activeSession.IsRunning)
                    {
                        activeSession.Stop();
                    }

                    log.WriteLine(
                        $"[热插拔] 源屏 {sourceMonitor.DeviceName} 已断开，等待用户从托盘选择新源");
                    tray?.SetStatusText($"源屏已断开 · 预览 #{previewMonitor.Index}");
                }
                else
                {
                    // 同一块屏回来了：Stop 过的会话会重启，句柄变化则重建
                    SwitchSource(source);
                }

                MonitorDescriptor? preview = current.FirstOrDefault(
                    monitor => monitor.DeviceName == previewMonitor.DeviceName);

                if (preview is null)
                {
                    preview = current.FirstOrDefault(monitor => monitor.IsPrimary) ?? current[0];
                    log.WriteLine($"[热插拔] 预览屏已断开，移回 #{preview.Index}");
                }

                previewMonitor = preview;

                (int x, int y, int width, int height) = WindowPlacement.GetBounds(previewWindow.Handle);
                (int nextX, int nextY, int nextWidth, int nextHeight) = PreviewLayout.FitToMonitor(
                    x,
                    y,
                    width,
                    height,
                    activeSession.Source.FrameSize.Width,
                    activeSession.Source.FrameSize.Height,
                    previewMonitor);
                WindowPlacement.Move(previewWindow.Handle, nextX, nextY, nextWidth, nextHeight);

                tray?.SetStatusText($"捕获 #{sourceMonitor.Index} · 预览 #{previewMonitor.Index}");
            }

            // WM_DISPLAYCHANGE 会连续到达多次，等显示配置稳定后再处理
            previewWindow.DisplayChanged += () =>
            {
                displayDebounce?.Stop();
                displayDebounce?.Dispose();
                displayDebounce = new System.Windows.Forms.Timer { Interval = 1200 };
                displayDebounce.Tick += (_, _) =>
                {
                    displayDebounce?.Stop();
                    displayDebounce?.Dispose();
                    displayDebounce = null;
                    HandleDisplayChange();
                };
                displayDebounce.Start();
            };

            tray = new TrayIconHost(
                logDirectory,
                $"捕获 #{sourceMonitor.Index} · 预览 #{previewMonitor.Index}",
                ToggleClickThrough,
                () => previewWindow.Visible,
                () => overlayEnabled,
                TogglePreview,
                ToggleOverlay,
                () => OpenPanel(),
                RequestExit,
                () => RunBenchmark(TimeSpan.FromSeconds(60), silent: false),
                () => monitors
                    .Select(monitor => new TrayMonitorItem(
                        $"#{monitor.Index} {(monitor.IsPrimary ? "主屏" : "副屏")} {monitor.Width}x{monitor.Height}",
                        monitor.Index == activeSession.Monitor.Index,
                        () => SelectSource(monitor)))
                    .ToList(),
                clickThroughAvailable: false);

            log.WriteLine("[托盘] 已创建，关闭窗口将收进托盘");

            captureExcluded = WindowCaptureExclusion.Apply(windowHandle);
            if (captureExcluded)
            {
                log.WriteLine("[窗口] 已从屏幕捕获中排除");
            }
            else
            {
                log.WriteLine("[窗口] 警告：系统不支持 WDA_EXCLUDEFROMCAPTURE，预览窗可能被拍进画面");
            }

            exitHotKey = TryCreateHotKey(
                HotKeyExit,
                GlobalHotKey.ControlAlt,
                VirtualKeyQ,
                "Ctrl+Alt+Q 退出",
                RequestExit,
                log)
                ?? TryCreateHotKey(
                    HotKeyExit,
                    GlobalHotKey.ControlShift,
                    VirtualKeyQ,
                    "Ctrl+Shift+Q 退出（备选）",
                    RequestExit,
                    log);

            try
            {
                session.Start();
                activeLoop.Start();
            }
            catch (Exception ex)
            {
                log.WriteLine($"[启动] 采集启动失败：{ex}");
                exiting = true;
                previewWindow.Close();
            }

            if (options.ShowPanel && !exiting)
            {
                OpenPanel();
                log.WriteLine("[面板] 已按启动参数打开主控面板");
            }

            if (options.BenchmarkSeconds is int benchmarkSeconds && benchmarkSeconds > 0)
            {
                // 无人值守测试：跑完写报告并退出，不弹窗
                RunBenchmark(TimeSpan.FromSeconds(benchmarkSeconds), silent: true);
            }

            if (options.FaultSimulationSeconds is int faultSeconds && faultSeconds > 0)
            {
                // 诊断参数：定时标记呈现链路失效，用于验证设备丢失重建
                var faultTimer = new System.Windows.Forms.Timer { Interval = faultSeconds * 1000 };
                faultTimer.Tick += (_, _) =>
                {
                    faultTimer.Stop();
                    faultTimer.Dispose();
                    previewHost.SimulateFault();
                };
                faultTimer.Start();
            }

            healthTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            healthTimer.Tick += (_, _) =>
            {
                if (previewHost.IsFaulted)
                {
                    RecoverPipeline();
                }
            };
            healthTimer.Start();

            Application.Run(previewWindow);
        }
        finally
        {
            // 关闭顺序必须与创建相反：先移除托盘，再停渲染，最后停采集与呈现
            healthTimer?.Stop();
            healthTimer?.Dispose();
            displayDebounce?.Stop();
            displayDebounce?.Dispose();
            tray?.Dispose();
            panel?.Dispose();
            loop?.Dispose();
            session?.Dispose();
            overlay?.Dispose();
            host?.Dispose();
            gpu?.Dispose();
            exitHotKey?.Dispose();
        }

        reporter.ReportFinal();
        log.WriteLine("Glimpse 已退出");
    }

    /// <summary>
    /// 恢复保存的预览窗位置；位置失效或首次启动时按源画面比例重新推导。
    /// 恢复时按当前源比例修正尺寸，避免源分辨率变化后出现黑边。
    /// </summary>
    private static (int X, int Y, int Width, int Height) ResolvePreviewBounds(
        AppSettings settings,
        MonitorDescriptor previewMonitor,
        (int Width, int Height) sourceSize,
        IReadOnlyList<MonitorDescriptor> monitors)
    {
        if (settings.Placement is { } saved && PreviewLayout.IsUsable(saved, monitors))
        {
            (int width, int height) = PreviewLayout.FitToAspect(
                saved.Width,
                saved.Height,
                sourceSize.Width,
                sourceSize.Height);

            return (saved.X, saved.Y, width, height);
        }

        return PreviewLayout.Compute(previewMonitor, sourceSize.Width, sourceSize.Height);
    }

    private static void SaveSettings(
        SettingsStore store,
        AppSettings current,
        PreviewWindow window,
        MonitorDescriptor sourceMonitor,
        MonitorDescriptor previewMonitor,
        D3DPreviewHost host,
        bool showOverlay,
        DiagnosticsLog log)
    {
        try
        {
            (int x, int y, int width, int height) = WindowPlacement.GetBounds(window.Handle);
            AppSettings updated = current with
            {
                SourceDeviceName = sourceMonitor.DeviceName,
                PreviewDeviceName = previewMonitor.DeviceName,
                SourceMonitorIndex = sourceMonitor.Index,
                PreviewMonitorIndex = previewMonitor.Index,
                Placement = new PreviewPlacement(x, y, width, height),
                ClickThrough = host.IsClickThrough,
                ShowLatencyOverlay = showOverlay,
            };

            store.Save(updated);
            log.WriteLine($"[设置] 已保存：{store.FilePath}");
        }
        catch (Exception ex)
        {
            // 保存失败不能影响退出路径
            log.WriteLine($"[设置] 保存失败：{ex.Message}");
        }
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
