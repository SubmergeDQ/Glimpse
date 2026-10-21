using Glimpse.Abstractions.Display;
using Glimpse.Interop;

namespace Glimpse.Display;

/// <summary>把 Win32 枚举结果适配成契约层接口。</summary>
internal sealed class MonitorEnumerator : IMonitorEnumerator
{
    public IReadOnlyList<MonitorDescriptor> Enumerate() => MonitorInterop.Enumerate();
}
