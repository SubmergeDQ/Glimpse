using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace Glimpse.Interop;

/// <summary>DirectComposition 设备创建。</summary>
internal static class CompositionInterop
{
    /// <summary>
    /// 用同一个 DXGI 设备创建合成器，保证交换链内容不需要跨设备拷贝。
    /// </summary>
    public static IDCompositionDevice CreateDevice(ID3D11Device device)
    {
        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        return DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
    }
}
