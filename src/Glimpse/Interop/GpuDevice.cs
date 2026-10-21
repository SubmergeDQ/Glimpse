using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace Glimpse.Interop;

/// <summary>
/// 采集与呈现共享的 D3D 设备。
/// 立即上下文不是线程安全的，所有 <c>CopyResource</c> 与绘制都必须持有 <see cref="ContextLock"/>。
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    private bool _disposed;

    public GpuDevice(
        ID3D11Device device,
        ID3D11DeviceContext context,
        FeatureLevel featureLevel,
        string adapterName,
        Luid adapterLuid)
    {
        Device = device;
        Context = context;
        FeatureLevel = featureLevel;
        AdapterName = adapterName;
        AdapterLuid = adapterLuid;
    }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    public FeatureLevel FeatureLevel { get; }

    public string AdapterName { get; }

    public Luid AdapterLuid { get; }

    public object ContextLock { get; } = new();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Context.Dispose();
        Device.Dispose();
    }
}
