using System.Runtime.InteropServices;
using Glimpse.Abstractions.Capture;
using Glimpse.Abstractions.Diagnostics;
using Glimpse.Abstractions.Display;
using Glimpse.Diagnostics;
using Glimpse.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Glimpse.Capture;

/// <summary>
/// Windows.Graphics.Capture 帧源。
/// 回调线程完成 Surface → 自有纹理的 CopyResource，并交替写入两个槽位；
/// 渲染线程永远只读“最新槽”，因此不需要等待队列，也不会出现累积延迟。
/// </summary>
internal sealed class WgcFrameSource : IFrameSource
{
    private readonly GpuDevice _gpu;
    private readonly MonitorDescriptor _monitor;
    private readonly IFrameClock _clock;
    private readonly FrameStats _stats;
    private readonly LatencyMonitor _latency;
    private readonly DiagnosticsLog _log;
    private readonly GraphicsCaptureItem _item;
    private readonly IDirect3DDevice _winrtDevice;

    // 回调、停止、尺寸重建都在这个锁内串行化；渲染线程不参与，避免影响呈现节拍
    private readonly object _callbackGate = new();
    private readonly List<ID3D11Texture2D> _retiredSlots = [];

    private ID3D11Texture2D?[] _slots = new ID3D11Texture2D?[2];
    private FrameEnvelope? _latestFrame;
    private int _latestSlot = -1;

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;

    private int _frameWidth;
    private int _frameHeight;
    private int _poolWidth;
    private int _poolHeight;
    private Format _slotFormat;
    private DirectXPixelFormat _poolFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private volatile bool _running;
    private volatile bool _disposed;
    private int _errorCount;

    public WgcFrameSource(
        GpuDevice gpu,
        MonitorDescriptor monitor,
        IFrameClock clock,
        FrameStats stats,
        LatencyMonitor latency,
        DiagnosticsLog log)
    {
        _gpu = gpu;
        _monitor = monitor;
        _clock = clock;
        _stats = stats;
        _latency = latency;
        _log = log;

        _winrtDevice = D3D11Interop.CreateWinRtDevice(gpu.Device);
        _item = WgcInterop.CreateItemForMonitor(monitor.Handle);

        _frameWidth = _item.Size.Width;
        _frameHeight = _item.Size.Height;
        _poolWidth = _frameWidth;
        _poolHeight = _frameHeight;

        // 源画面默认按 SDR 处理；帧池创建失败时会切到 FP16
        _slotFormat = Format.B8G8R8A8_UNorm;
        CreateSlots(_frameWidth, _frameHeight, _slotFormat);
    }

    public (int Width, int Height) FrameSize =>
        (Volatile.Read(ref _frameWidth), Volatile.Read(ref _frameHeight));

    public void StartCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_callbackGate)
        {
            if (_running)
            {
                return;
            }

            CreatePipeline();
            _running = true;
        }

        try
        {
            _session!.StartCapture();
            _log.WriteLine($"[采集] 开始捕获显示器 #{_monitor.Index} {_frameWidth}x{_frameHeight}");
        }
        catch
        {
            StopCapture();
            throw;
        }
    }

    public void StopCapture()
    {
        GraphicsCaptureSession? session;
        Direct3D11CaptureFramePool? pool;

        lock (_callbackGate)
        {
            _running = false;
            session = _session;
            pool = _framePool;
            _session = null;
            _framePool = null;

            if (pool is not null)
            {
                pool.FrameArrived -= OnFrameArrived;
            }

            Interlocked.Exchange(ref _latestFrame, null);
            _latestSlot = -1;
        }

        // 锁外释放，避免在等待回调线程时持锁过久
        session?.Dispose();
        pool?.Dispose();
    }

    public bool TryTakeLatestFrame(out CapturedFrame frame)
    {
        FrameEnvelope? envelope = Interlocked.Exchange(ref _latestFrame, null);
        if (envelope is null)
        {
            frame = default;
            return false;
        }

        frame = envelope.Frame;
        return true;
    }

    private void CreatePipeline()
    {
        _session?.Dispose();
        _framePool?.Dispose();

        _framePool = CreateFramePool();
        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_item);

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _session.IsCursorCaptureEnabled = true;
        }

        // 去掉被捕获屏上的黄色隐私边框，否则投屏观众会看到
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            try
            {
                _session.IsBorderRequired = false;
            }
            catch (Exception ex)
            {
                _log.WriteLine($"[采集] 关闭捕获边框失败：{ex.Message}");
            }
        }
    }

    private Direct3D11CaptureFramePool CreateFramePool()
    {
        var size = new Windows.Graphics.SizeInt32
        {
            Width = _frameWidth,
            Height = _frameHeight,
        };

        try
        {
            Direct3D11CaptureFramePool pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                size);

            // 上一次启动可能因 HDR 降级成 FP16，这里要确保槽位格式与帧池一致
            _poolFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
            if (_slotFormat != Format.B8G8R8A8_UNorm)
            {
                _slotFormat = Format.B8G8R8A8_UNorm;
                RecreateSlots(_frameWidth, _frameHeight, _slotFormat);
            }

            return pool;
        }
        catch (Exception bgraFailure)
        {
            // HDR 显示器可能拒绝 SDR 格式，换成 FP16 再试一次
            _log.WriteLine($"[采集] BGRA8 帧池创建失败（{bgraFailure.Message}），改用 FP16");

            _poolFormat = DirectXPixelFormat.R16G16B16A16Float;
            _slotFormat = Format.R16G16B16A16_Float;
            RecreateSlots(_frameWidth, _frameHeight, _slotFormat);

            try
            {
                return Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _winrtDevice,
                    _poolFormat,
                    2,
                    size);
            }
            catch (Exception fp16Failure)
            {
                throw new InvalidOperationException(
                    "无法创建 WGC 帧池（BGRA8 与 FP16 均失败）。",
                    new AggregateException(bgraFailure, fp16Failure));
            }
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        bool recreatePool;
        int contentWidth;
        int contentHeight;

        lock (_callbackGate)
        {
            if (_disposed || !_running)
            {
                return;
            }

            using Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            // ContentSize 是源画面的逻辑尺寸，槽位尺寸则以帧 Surface 的实际描述为准
            contentWidth = frame.ContentSize.Width;
            contentHeight = frame.ContentSize.Height;
            recreatePool = contentWidth != _poolWidth || contentHeight != _poolHeight;

            try
            {
                ProcessFrame(frame);
            }
            catch (Exception ex)
            {
                // 采集线程上的异常不能上抛，否则会终止进程；前几次保留日志便于定位
                if (Interlocked.Increment(ref _errorCount) <= 3)
                {
                    _log.WriteLine($"[采集] 处理帧失败：{ex.Message}");
                }
            }
        }

        // 帧已 Dispose 后再重建帧池，避免新池回收正在使用的 Surface
        if (recreatePool)
        {
            try
            {
                sender.Recreate(_winrtDevice, _poolFormat, 2, new Windows.Graphics.SizeInt32
                {
                    Width = contentWidth,
                    Height = contentHeight,
                });
                Volatile.Write(ref _poolWidth, contentWidth);
                Volatile.Write(ref _poolHeight, contentHeight);
                _log.WriteLine($"[采集] 源尺寸变化，帧池已重建为 {contentWidth}x{contentHeight}");
            }
            catch (Exception ex)
            {
                _log.WriteLine($"[采集] 重建帧池失败：{ex.Message}");
            }
        }
    }

    private void ProcessFrame(Direct3D11CaptureFrame frame)
    {
        double capturedAtSeconds = frame.SystemRelativeTime.TotalSeconds;
        double ageMs = Math.Max(0, (_clock.NowSeconds - capturedAtSeconds) * 1000.0);

        using IDirect3DDxgiInterfaceAccess access = GetDxgiAccess(frame.Surface);
        using ID3D11Texture2D sourceTexture = access.GetInterface<ID3D11Texture2D>();
        Texture2DDescription description = sourceTexture.Description;

        int width = (int)description.Width;
        int height = (int)description.Height;

        if (width != _frameWidth || height != _frameHeight || description.Format != _slotFormat)
        {
            RecreateSlots(width, height, description.Format);
        }

        int writeSlot = _latestSlot == 0 ? 1 : 0;
        ID3D11Texture2D destination = _slots[writeSlot]
            ?? throw new InvalidOperationException("纹理槽未初始化。");

        lock (_gpu.ContextLock)
        {
            _gpu.Context.CopyResource(destination, sourceTexture);
        }

        _latestSlot = writeSlot;
        Interlocked.Exchange(
            ref _latestFrame,
            new FrameEnvelope(new CapturedFrame(
                destination,
                width,
                height,
                description.Format,
                capturedAtSeconds)));

        _stats.RecordCaptured();
        _latency.Record(ageMs);
    }

    // IDirect3DDxgiInterfaceAccess 的 IID，WinRT Surface 通过该接口暴露原生 D3D 纹理
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    /// <summary>
    /// IDirect3DSurface 是 WinRT 对象，CsWinRT 的 As&lt;T&gt; 无法直接转成 Vortice 的 COM 接口。
    /// 必须先对 ABI 指针做 QueryInterface，不能把 IInspectable 指针直接当作接口 vtable。
    /// </summary>
    private static IDirect3DDxgiInterfaceAccess GetDxgiAccess(IDirect3DSurface surface)
    {
        IntPtr sourcePointer = ((IWinRTObject)surface).NativeObject.ThisPtr;
        Guid iid = DxgiInterfaceAccessIid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(sourcePointer, ref iid, out IntPtr accessPointer));

        // QueryInterface 已返回 AddRef 后的引用，包装器负责释放
        return new IDirect3DDxgiInterfaceAccess(accessPointer);
    }

    private void RecreateSlots(int width, int height, Format format)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"无效的源尺寸 {width}x{height}。");
        }

        // 旧槽不立即释放：渲染线程可能仍持有其中一帧的纹理引用
        foreach (ID3D11Texture2D? slot in _slots)
        {
            if (slot is not null)
            {
                _retiredSlots.Add(slot);
            }
        }

        _slots = new ID3D11Texture2D?[2];
        _latestSlot = -1;
        Interlocked.Exchange(ref _latestFrame, null);
        Interlocked.Exchange(ref _frameWidth, width);
        Interlocked.Exchange(ref _frameHeight, height);
        _slotFormat = format;

        CreateSlots(width, height, format);
    }

    private void CreateSlots(int width, int height, Format format)
    {
        var description = new Texture2DDescription(
            format,
            (uint)width,
            (uint)height,
            1,
            1,
            BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1,
            0,
            ResourceOptionFlags.None);

        _slots[0] = _gpu.Device.CreateTexture2D(description);
        _slots[1] = _gpu.Device.CreateTexture2D(description);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopCapture();

        lock (_callbackGate)
        {
            foreach (ID3D11Texture2D? slot in _slots)
            {
                slot?.Dispose();
            }

            _slots = new ID3D11Texture2D?[2];

            foreach (ID3D11Texture2D retired in _retiredSlots)
            {
                retired.Dispose();
            }

            _retiredSlots.Clear();
        }

        _winrtDevice.Dispose();

        // GraphicsCaptureItem 没有 IClosable，交给 GC 释放 COM 引用
        GC.KeepAlive(_item);
    }

    private sealed class FrameEnvelope(CapturedFrame frame)
    {
        public CapturedFrame Frame { get; } = frame;
    }
}
