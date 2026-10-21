using Glimpse.Interop;

namespace Glimpse.Platform;

/// <summary>
/// 窗口几何操作。使用物理像素，绕开 WinForms 的 DIP 换算。
/// </summary>
internal static class WindowPlacement
{
    public static void Place(IntPtr hwnd, int x, int y, int width, int height) =>
        WindowInterop.PlaceWindow(hwnd, x, y, width, height);

    public static (int Width, int Height) GetClientSize(IntPtr hwnd) =>
        WindowInterop.GetClientSize(hwnd);
}
