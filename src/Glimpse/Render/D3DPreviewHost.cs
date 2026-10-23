using Glimpse.Abstractions.Capture;
using Glimpse.Abstractions.Preview;
using Glimpse.Diagnostics;
using Glimpse.Interop;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Glimpse.Render;

/// <summary>
/// D3D11 交换链 + DirectComposition 呈现器。
/// 窗口本身没有 DWM 重定向表面，全部可见像素来自这里的交换链。
/// </summary>
internal sealed class D3DPreviewHost : IPreviewHost
{
    private const string ShaderSource = """
        struct VSOutput
        {
            float4 position : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        VSOutput VSMain(uint vertexId : SV_VertexID)
        {
            // Fullscreen triangle, no vertex buffer required.
            float2 uv = float2((float)((vertexId << 1) & 2), (float)(vertexId & 2));
            VSOutput output;
            output.position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
            output.uv = uv;
            return output;
        }

        Texture2D<float4> SourceTexture : register(t0);
        SamplerState SourceSampler : register(s0);

        float4 PSPresent(VSOutput input) : SV_Target
        {
            float3 color = SourceTexture.Sample(SourceSampler, input.uv).rgb;
            return float4(color, 1.0);
        }

        float4 PSTest(VSOutput input) : SV_Target
        {
            float2 cell = floor(input.uv * 16.0);
            float checker = fmod(cell.x + cell.y, 2.0);
            float3 color = lerp(float3(0.10, 0.12, 0.16), float3(0.18, 0.42, 0.72), checker);

            // Border confirms the viewport follows window resizes.
            float border = step(input.uv.x, 0.004)
                + step(0.996, input.uv.x)
                + step(input.uv.y, 0.004)
                + step(0.996, input.uv.y);
            color = lerp(color, float3(0.95, 0.35, 0.10), saturate(border));

            return float4(color, 1.0);
        }
        """;

    // DXGI 结果码直接用常量，避免不同版本绑定的枚举类型差异
    private const int WasStillDrawing = unchecked((int)0x887A000A);
    private const int DeviceRemoved = unchecked((int)0x887A0005);
    private const int DeviceReset = unchecked((int)0x887A0007);

    private readonly GpuDevice _gpu;
    private readonly IntPtr _hwnd;
    private readonly FrameStats _stats;
    private readonly DiagnosticsLog _log;
    private readonly object _renderLock = new();
    private readonly Dictionary<IntPtr, ID3D11ShaderResourceView> _srvCache = [];
    private readonly ID3D11ShaderResourceView[] _srvBinding = new ID3D11ShaderResourceView[1];

    private readonly IDCompositionDevice _compositionDevice;
    private readonly IDCompositionTarget _compositionTarget;
    private readonly IDCompositionVisual _compositionVisual;

    private readonly IDXGISwapChain1 _swapChain;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _presentPixelShader;
    private readonly ID3D11PixelShader _testPixelShader;
    private readonly ID3D11SamplerState _sampler;
    private ID3D11RenderTargetView _renderTargetView;

    private CapturedFrame? _lastFrame;
    private int _width;
    private int _height;
    private bool _clickThrough;
    private volatile bool _faulted;
    private bool _disposed;

    public D3DPreviewHost(
        GpuDevice gpu,
        IntPtr hwnd,
        int width,
        int height,
        FrameStats stats,
        DiagnosticsLog log)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        _gpu = gpu;
        _hwnd = hwnd;
        _stats = stats;
        _log = log;
        _width = width;
        _height = height;

        // 着色器模型 4.0 在特性级别 10_0 及以上都可用，避免降级设备卡在创建阶段
        ReadOnlyMemory<byte> vertexBytecode = Compiler.Compile(
            ShaderSource, "VSMain", "GlimpsePreview.hlsl", "vs_4_0");
        ReadOnlyMemory<byte> presentBytecode = Compiler.Compile(
            ShaderSource, "PSPresent", "GlimpsePreview.hlsl", "ps_4_0");
        ReadOnlyMemory<byte> testBytecode = Compiler.Compile(
            ShaderSource, "PSTest", "GlimpsePreview.hlsl", "ps_4_0");

        _vertexShader = gpu.Device.CreateVertexShader(vertexBytecode.Span, null);
        _presentPixelShader = gpu.Device.CreatePixelShader(presentBytecode.Span, null);
        _testPixelShader = gpu.Device.CreatePixelShader(testBytecode.Span, null);

        _sampler = gpu.Device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear,
            TextureAddressMode.Clamp,
            0,
            1,
            ComparisonFunction.Never,
            0,
            float.MaxValue));

        _compositionDevice = CompositionInterop.CreateDevice(gpu.Device);
        _compositionDevice.CreateTargetForHwnd(hwnd, true, out _compositionTarget).CheckError();
        _compositionVisual = _compositionDevice.CreateVisual();

        _swapChain = CreateSwapChain(gpu, width, height);

        // 合成交换链不支持 IDXGISwapChain2.SetMaximumFrameLatency，改为设备级设置
        using (IDXGIDevice1 dxgiDevice1 = gpu.Device.QueryInterface<IDXGIDevice1>())
        {
            dxgiDevice1.MaximumFrameLatency = 1;
        }

        _compositionVisual.SetContent(_swapChain).CheckError();
        _compositionTarget.SetRoot(_compositionVisual).CheckError();
        _compositionDevice.Commit().CheckError();

        _renderTargetView = CreateRenderTargetView();

        // 采集启动前先显示测试图案，证明设备、交换链、合成器这条链路是通的
        lock (_renderLock)
        {
            DrawTestPatternLocked();
        }
    }

    public bool IsClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value)
            {
                return;
            }

            _clickThrough = value;
            WindowInterop.SetClickThrough(_hwnd, value);
            _log.WriteLine($"[窗口] 点击穿透已{(value ? "开启" : "关闭")}");
        }
    }

    /// <summary>呈现链路已失效（通常是设备移除）。调用方应停止渲染线程。</summary>
    public bool IsFaulted => _faulted;

    /// <summary>
    /// 仅供 <c>--simulate-fault</c> 诊断参数使用：把链路标记为失效，
    /// 用于验证设备丢失后的重建流程。真实设备移除由 Present 结果码触发。
    /// </summary>
    internal void SimulateFault()
    {
        _faulted = true;
        _log.WriteLine("[呈现] 模拟设备丢失，等待健康检查重建");
    }

    public void Present(in CapturedFrame frame)
    {
        lock (_renderLock)
        {
            if (_disposed || _faulted)
            {
                return;
            }

            _lastFrame = frame;

            // 采集线程的 CopyResource 也使用同一个立即上下文，这里必须串行
            lock (_gpu.ContextLock)
            {
                DrawFrameLocked(frame, recordStats: true);
            }
        }
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        lock (_renderLock)
        {
            if (_disposed || _swapChain is null || (width == _width && height == _height))
            {
                return;
            }

            try
            {
                lock (_gpu.ContextLock)
                {
                    ID3D11DeviceContext context = _gpu.Context;
                    context.UnsetRenderTargets();
                    context.Flush();

                    _renderTargetView.Dispose();
                    _swapChain.ResizeBuffers(
                        2,
                        (uint)width,
                        (uint)height,
                        Format.B8G8R8A8_UNorm,
                        SwapChainFlags.None).CheckError();

                    _width = width;
                    _height = height;
                    _renderTargetView = CreateRenderTargetView();

                    // 窗口尺寸变化后立即重绘上一帧，源画面静止时也不会留白；
                    // 这是重绘而非新帧，不计入呈现统计
                    if (_lastFrame is { } frame)
                    {
                        DrawFrameLocked(frame, recordStats: false);
                    }
                    else
                    {
                        DrawTestPatternLocked();
                    }
                }

                _compositionDevice.Commit().CheckError();
            }
            catch (Exception ex)
            {
                _faulted = true;
                _log.WriteLine($"[呈现] 调整尺寸失败：{ex.Message}");
            }
        }
    }

    public void Show()
    {
        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }

            _compositionDevice.Commit().CheckError();
        }
    }

    private static IDXGISwapChain1 CreateSwapChain(GpuDevice gpu, int width, int height)
    {
        using IDXGIDevice dxgiDevice = gpu.Device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

        var description = new SwapChainDescription1(
            (uint)width,
            (uint)height,
            Format.B8G8R8A8_UNorm,
            false,
            Usage.RenderTargetOutput,
            2,
            Scaling.Stretch,
            SwapEffect.FlipDiscard,
            AlphaMode.Ignore,
            SwapChainFlags.None);

        return factory.CreateSwapChainForComposition(gpu.Device, description, null);
    }

    private ID3D11RenderTargetView CreateRenderTargetView()
    {
        using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        return _gpu.Device.CreateRenderTargetView(backBuffer, null);
    }

    private ID3D11ShaderResourceView GetShaderResourceView(ID3D11Texture2D texture)
    {
        IntPtr key = texture.NativePointer;
        if (!_srvCache.TryGetValue(key, out ID3D11ShaderResourceView? view))
        {
            view = _gpu.Device.CreateShaderResourceView(texture, null);
            _srvCache[key] = view;
        }

        return view;
    }

    private void DrawFrameLocked(in CapturedFrame frame, bool recordStats)
    {
        ID3D11DeviceContext context = _gpu.Context;
        context.OMSetRenderTargets(_renderTargetView, null);
        context.ClearRenderTargetView(_renderTargetView, new Color4(0, 0, 0, 1));

        Viewport viewport = AspectFit.Compute(frame.Width, frame.Height, _width, _height);
        context.RSSetViewport(viewport);

        _srvBinding[0] = GetShaderResourceView(frame.Texture);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_presentPixelShader);
        context.PSSetSampler(0, _sampler);
        context.PSSetShaderResources(0, 1, _srvBinding);
        context.Draw(3, 0);

        PresentSwapChain(recordStats);
    }

    private void DrawTestPatternLocked()
    {
        ID3D11DeviceContext context = _gpu.Context;
        context.OMSetRenderTargets(_renderTargetView, null);
        context.ClearRenderTargetView(_renderTargetView, new Color4(0, 0, 0, 1));
        context.RSSetViewport(new Viewport(0, 0, _width, _height, 0, 1));
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_testPixelShader);
        context.Draw(3, 0);

        PresentSwapChain(recordStats: false);
    }

    private void PresentSwapChain(bool recordStats)
    {
        SharpGen.Runtime.Result result = _swapChain.Present(0, PresentFlags.DoNotWait);
        if (result.Success)
        {
            if (recordStats)
            {
                _stats.RecordPresented();
            }

            return;
        }

        if (result.Code == WasStillDrawing)
        {
            // 合成器仍在消费上一帧，这一帧直接丢弃，不排队
            if (recordStats)
            {
                _stats.RecordDropped();
            }

            return;
        }

        if (recordStats)
        {
            _stats.RecordDropped();
        }

        if (result.Code is DeviceRemoved or DeviceReset)
        {
            _faulted = true;
            _log.WriteLine($"[呈现] 设备丢失（0x{result.Code:X8}），渲染已停止；请重启 Glimpse");
            return;
        }

        _log.WriteLine($"[呈现] Present 失败：0x{result.Code:X8}");
    }

    public void Dispose()
    {
        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (ID3D11ShaderResourceView view in _srvCache.Values)
            {
                view.Dispose();
            }

            _srvCache.Clear();
            _renderTargetView.Dispose();
            _sampler.Dispose();
            _testPixelShader.Dispose();
            _presentPixelShader.Dispose();
            _vertexShader.Dispose();
            _compositionVisual.Dispose();
            _compositionTarget.Dispose();
            _compositionDevice.Dispose();
            _swapChain.Dispose();
        }
    }
}
