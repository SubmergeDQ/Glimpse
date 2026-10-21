using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;

namespace CaptureProbe;

internal sealed record FrameData(byte[] Pixels, int Width, int Height, double CaptureSeconds, double AgeMs);

/// <summary>
/// 最小 WGC 捕获会话：按显示器捕获，统计每帧从“被系统捕获”到“应用拿到”的年龄。
/// </summary>
internal sealed class CaptureEngine : IDisposable
{
    [DllImport("d3d11.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;

    private readonly object _statsLock = new();
    private readonly List<double> _ages = new();
    private readonly Stopwatch _window = Stopwatch.StartNew();
    private readonly System.Threading.Timer _reporter;
    private int _framesInWindow;
    private long _totalFrames;
    private FrameData? _latestFrame;
    private int _readbackFailed;

    /// <summary>UI 线程取走最新一帧，天然丢弃积压帧。</summary>
    public FrameData? TakeLatestFrame() => Interlocked.Exchange(ref _latestFrame, null);

    public CaptureEngine(GraphicsCaptureItem item)
    {
        FeatureLevel[] levels =
        [
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        ];

        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            levels,
            out _device,
            out FeatureLevel featureLevel,
            out _context);
        result.CheckError();

        using (IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        {
            CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectable);
            try
            {
                _winrtDevice = MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            item.Size);
        _framePool.FrameArrived += (pool, _) => OnFrameArrived(pool);

        _session = _framePool.CreateCaptureSession(item);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _session.IsCursorCaptureEnabled = true;
        }

        // 去掉系统在被捕获屏上绘制的黄色隐私边框，否则观众会在投屏上看到
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            try
            {
                _session.IsBorderRequired = false;
                Console.WriteLine("已关闭捕获边框");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"关闭捕获边框失败: {ex.Message}");
            }
        }

        _reporter = new System.Threading.Timer(_ => Report(), null, 1000, 1000);

        Console.WriteLine($"D3D 特性级别: {featureLevel}");
        Console.WriteLine($"捕获尺寸: {item.Size.Width}x{item.Size.Height}");
    }

    public void Start()
    {
        _session.StartCapture();
        Console.WriteLine("捕获已启动。Ctrl+C 结束。");
        Console.WriteLine("时间     FPS   帧年龄(ms) min/avg/p95/max");
    }

    private async void OnFrameArrived(Direct3D11CaptureFramePool sender)
    {
        Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
        if (frame is null)
        {
            return;
        }

        // SystemRelativeTime 与 QPC 同源（系统启动起算），可直接比较得到帧年龄
        double nowSeconds = CaptureInterop.QueryPerfCounterSeconds();
        double ageMs = (nowSeconds - frame.SystemRelativeTime.TotalSeconds) * 1000.0;
        double captureSeconds = frame.SystemRelativeTime.TotalSeconds;

        lock (_statsLock)
        {
            _ages.Add(ageMs);
            _framesInWindow++;
            _totalFrames++;
        }

        try
        {
            using (frame)
            {
                using SoftwareBitmap bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                    frame.Surface,
                    BitmapAlphaMode.Premultiplied);

                int width = bitmap.PixelWidth;
                int height = bitmap.PixelHeight;
                uint capacity = (uint)(width * height * 4);

                var buffer = new Windows.Storage.Streams.Buffer(capacity) { Length = capacity };
                bitmap.CopyToBuffer(buffer);

                byte[] pixels = new byte[(int)capacity];
                using (DataReader reader = DataReader.FromBuffer(buffer))
                {
                    reader.ReadBytes(pixels);
                }

                Interlocked.Exchange(ref _latestFrame, new FrameData(pixels, width, height, captureSeconds, ageMs));
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _readbackFailed) <= 3)
            {
                Console.WriteLine($"[读回失败] {ex.Message}");
            }
        }
    }

    private void Report()
    {
        double[] snapshot;
        int frames;
        lock (_statsLock)
        {
            if (_window.ElapsedMilliseconds < 1000)
            {
                return;
            }

            snapshot = _ages.ToArray();
            frames = _framesInWindow;
            _ages.Clear();
            _framesInWindow = 0;
            _window.Restart();
        }

        if (snapshot.Length == 0)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss}  0 fps   (画面静止，WGC 不产生新帧)");
            return;
        }

        Array.Sort(snapshot);
        double avg = snapshot.Average();
        double p95 = snapshot[Math.Min(snapshot.Length - 1, (int)(snapshot.Length * 0.95))];
        Console.WriteLine(
            $"{DateTime.Now:HH:mm:ss} {frames,4} fps  " +
            $"{snapshot[0],6:F1} / {avg,5:F1} / {p95,5:F1} / {snapshot[^1],6:F1}   " +
            $"累计 {_totalFrames} 帧");
    }

    public void Dispose()
    {
        _reporter.Dispose();
        _session.Dispose();
        _framePool.Dispose();
        _winrtDevice.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
