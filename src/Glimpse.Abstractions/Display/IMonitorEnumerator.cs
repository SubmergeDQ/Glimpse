namespace Glimpse.Abstractions.Display;

/// <summary>
/// 显示器描述。坐标使用虚拟桌面坐标系，副屏可能在主屏左侧或上方，因此会有负值。
/// </summary>
/// <param name="Handle">HMONITOR 原始句柄，仅用于创建捕获项。</param>
/// <param name="Index">枚举序号，界面展示与配置持久化使用。</param>
/// <param name="IsPrimary">是否为主显示器。</param>
/// <param name="X">虚拟桌面坐标系中的左边界，副屏位于主屏左侧时为负值。</param>
/// <param name="Y">虚拟桌面坐标系中的上边界。</param>
/// <param name="Width">显示器宽度（物理像素）。</param>
/// <param name="Height">显示器高度（物理像素）。</param>
/// <param name="DeviceName">系统设备名，如 <c>\\.\DISPLAY2</c>。作为持久化主键，序号仅作兜底。</param>
public sealed record MonitorDescriptor(
    IntPtr Handle,
    int Index,
    bool IsPrimary,
    int X,
    int Y,
    int Width,
    int Height,
    string DeviceName = "");

/// <summary>显示器枚举。抽象出来便于在无显示器环境下做单元测试。</summary>
public interface IMonitorEnumerator
{
    IReadOnlyList<MonitorDescriptor> Enumerate();
}
