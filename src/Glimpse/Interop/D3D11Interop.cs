using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Glimpse.Interop;

/// <summary>
/// D3D11 设备创建与 WinRT 设备桥接。
/// 必须显式选择显示器所在的适配器，不能用默认适配器掩盖混合显卡问题。
/// </summary>
internal static class D3D11Interop
{
    private static readonly FeatureLevel[] PreferredLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    ];

    // 部分环境对 11_1 直接返回 E_INVALIDARG，需要去掉后重试
    private static readonly FeatureLevel[] FallbackLevels =
    [
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    ];

    [DllImport("d3d11.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>把 D3D11 设备包装成 WGC 需要的 WinRT 设备。</summary>
    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectable);
        try
        {
            return MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>查找显示器所属适配器的 LUID；找不到时返回 null。</summary>
    public static Luid? TryFindAdapterLuid(IntPtr monitor)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            if (factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Failure || adapter is null)
            {
                return null;
            }

            using (adapter)
            {
                if (AdapterContainsMonitor(adapter, monitor))
                {
                    return adapter.Description1.Luid;
                }
            }
        }
    }

    /// <summary>
    /// 优先使用系统推荐的高性能适配器，其次使用指定显示器所属适配器，最后退回适配器 0。
    /// 混合显卡机器上按显示器选择可能落到无法合成 DirectComposition 内容的核显，
    /// 表现为预览窗全黑；高性能适配器的内容在核显与独显输出上都实测可显示。
    /// </summary>
    public static GpuDevice CreateDevice(IntPtr? preferredMonitor)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        IDXGIAdapter1? selectedAdapter = TryGetHighPerformanceAdapter();
        string adapterName = "未知适配器";
        string adapterSelection = "高性能推荐";
        Luid adapterLuid = default;

        if (selectedAdapter is null
            && preferredMonitor is { } monitor
            && TryFindAdapter(factory, monitor, out IDXGIAdapter1? matched)
            && matched is not null)
        {
            selectedAdapter = matched;
            adapterSelection = "显示器所属（降级）";
        }

        if (selectedAdapter is not null)
        {
            adapterName = selectedAdapter.Description1.Description;
            adapterLuid = selectedAdapter.Description1.Luid;
        }

        if (selectedAdapter is null)
        {
            // 没有匹配输出（例如虚拟显示驱动）时退回首个适配器
            factory.EnumAdapters1(0, out selectedAdapter).CheckError();
            if (selectedAdapter is null)
            {
                throw new InvalidOperationException("系统中没有可用的 DXGI 适配器。");
            }

            adapterName = selectedAdapter.Description1.Description;
            adapterLuid = selectedAdapter.Description1.Luid;
            adapterSelection = "首个可用（降级）";
        }

        try
        {
            // 显式传入适配器时 DriverType 必须是 Unknown，否则返回 E_INVALIDARG
            SharpGen.Runtime.Result result = D3D11.D3D11CreateDevice(
                selectedAdapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                PreferredLevels,
                out ID3D11Device? device,
                out FeatureLevel featureLevel,
                out ID3D11DeviceContext? context);

            if (result.Failure)
            {
                // 仅 11_1 不支持的机器会走到这里，去掉该级别重试一次
                result = D3D11.D3D11CreateDevice(
                    selectedAdapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    FallbackLevels,
                    out device,
                    out featureLevel,
                    out context);
            }

            result.CheckError();

            if (device is null || context is null)
            {
                throw new InvalidOperationException("D3D11CreateDevice 未返回有效设备。");
            }

            return new GpuDevice(device, context, featureLevel, adapterName, adapterLuid, adapterSelection);
        }
        finally
        {
            selectedAdapter.Dispose();
        }
    }

    private static IDXGIAdapter1? TryGetHighPerformanceAdapter()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
        {
            return null;
        }

        try
        {
            using IDXGIFactory6 factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
            SharpGen.Runtime.Result result = factory.EnumAdapterByGpuPreference<IDXGIAdapter1>(
                0,
                GpuPreference.HighPerformance,
                out IDXGIAdapter1? adapter);

            if (result.Success && adapter is not null)
            {
                return adapter;
            }

            adapter?.Dispose();
            return null;
        }
        catch (Exception)
        {
            // 旧系统或驱动不支持该接口时退回按显示器匹配
            return null;
        }
    }

    private static bool TryFindAdapter(IDXGIFactory1 factory, IntPtr monitor, out IDXGIAdapter1? adapter)
    {
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            if (factory.EnumAdapters1(adapterIndex, out adapter).Failure || adapter is null)
            {
                return false;
            }

            if (AdapterContainsMonitor(adapter, monitor))
            {
                return true;
            }

            adapter.Dispose();
        }
    }

    private static bool AdapterContainsMonitor(IDXGIAdapter1 adapter, IntPtr monitor)
    {
        for (uint outputIndex = 0; ; outputIndex++)
        {
            if (adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Failure || output is null)
            {
                return false;
            }

            using (output)
            {
                if (output.Description.Monitor == monitor)
                {
                    return true;
                }
            }
        }
    }
}
