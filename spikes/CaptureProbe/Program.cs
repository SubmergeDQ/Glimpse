namespace CaptureProbe;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();

        List<CaptureInterop.MonitorTarget> monitors = CaptureInterop.EnumerateMonitors();
        Console.WriteLine("检测到显示器:");
        foreach (CaptureInterop.MonitorTarget monitor in monitors)
        {
            Console.WriteLine($"  {monitor}");
        }

        if (args.Contains("--list"))
        {
            return;
        }

        int index = ResolveMonitorIndex(args, monitors);
        CaptureInterop.MonitorTarget target = monitors[index];
        Console.WriteLine();
        Console.WriteLine($"目标显示器: {target}");

        Windows.Graphics.Capture.GraphicsCaptureItem item;
        try
        {
            item = CaptureInterop.CreateItemForMonitor(target.Handle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"创建捕获项失败: {ex}");
            return;
        }

        using var engine = new CaptureEngine(item);

        CaptureInterop.MonitorTarget primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

        // 信号源铺满被捕获屏，模拟真实的投屏输出
        var signalBounds = new Rectangle(
            target.X,
            target.Y,
            target.Width,
            target.Height);
        using var signal = new SignalWindow(signalBounds);
        signal.Show();

        // 预览窗放在主屏，按源画面比例预留黑边
        int previewHeight = Math.Min(primary.Height * 7 / 10, 1000);
        int previewWidth = (int)(previewHeight * (double)target.Width / target.Height);
        int maxWidth = primary.Width * 3 / 5;
        if (previewWidth > maxWidth)
        {
            previewWidth = maxWidth;
            previewHeight = (int)(previewWidth * (double)target.Height / target.Width);
        }

        var previewBounds = new Rectangle(
            primary.X + primary.Width - previewWidth - 40,
            primary.Y + primary.Height - previewHeight - 40,
            previewWidth,
            previewHeight);
        using var preview = new PreviewForm(engine, previewBounds);
        preview.Show();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Application.Exit();
        };

        engine.Start();

        bool selfTest = args.Contains("--selftest");
        if (selfTest)
        {
            var selfTestTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            selfTestTimer.Tick += (_, _) =>
            {
                selfTestTimer.Stop();
                selfTestTimer.Dispose();
                preview.RunClickThroughSelfTest();
                Application.Exit();
            };
            selfTestTimer.Start();
        }

        // 自动化验证时用 --seconds N 定时退出，手动测试时省略即常驻
        int seconds = ResolveSeconds(args);
        if (seconds > 0)
        {
            var shutdown = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
            shutdown.Tick += (_, _) =>
            {
                shutdown.Stop();
                shutdown.Dispose();
                Application.Exit();
            };
            shutdown.Start();
        }

        Application.Run();
    }

    private static int ResolveSeconds(string[] args)
    {
        int marker = Array.IndexOf(args, "--seconds");
        if (marker >= 0 && marker + 1 < args.Length && int.TryParse(args[marker + 1], out int seconds))
        {
            return Math.Clamp(seconds, 1, 3600);
        }

        return 0;
    }

    private static int ResolveMonitorIndex(string[] args, List<CaptureInterop.MonitorTarget> monitors)
    {
        int marker = Array.IndexOf(args, "--monitor");
        if (marker >= 0 && marker + 1 < args.Length && int.TryParse(args[marker + 1], out int requested))
        {
            if (requested >= 0 && requested < monitors.Count)
            {
                return requested;
            }

            Console.WriteLine($"--monitor {requested} 超出范围，改用默认。");
        }

        // 默认优先选副屏，没有副屏时退回主屏
        int secondary = monitors.FindIndex(m => !m.IsPrimary);
        return secondary >= 0 ? secondary : 0;
    }
}
