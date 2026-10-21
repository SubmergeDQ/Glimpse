using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Glimpse.Abstractions.Capture;

/// <summary>
/// 一帧捕获结果。
/// 纹理所有权属于帧源：消费方只能读取，不得释放，也不得跨帧长期持有，
/// 因为下一帧可能复用同一块纹理。
/// </summary>
/// <param name="Texture">GPU 纹理，始终驻留显存，不经过内存中转。</param>
/// <param name="Width">纹理宽度（像素）。</param>
/// <param name="Height">纹理高度（像素）。</param>
/// <param name="Format">像素格式，跨屏时可能是 BGRA8 或 HDR 浮点格式。</param>
/// <param name="CapturedAtSeconds">系统捕获时刻，使用 <see cref="Diagnostics.IFrameClock"/> 同一时间基准。</param>
public readonly record struct CapturedFrame(
    ID3D11Texture2D Texture,
    int Width,
    int Height,
    Format Format,
    double CapturedAtSeconds);
