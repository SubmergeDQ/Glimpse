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

    public static (int X, int Y, int Width, int Height) GetBounds(IntPtr hwnd) =>
        WindowInterop.GetWindowBounds(hwnd);

    /// <summary>窗口所在显示器的有效 DPI，96 表示 100%。</summary>
    public static int GetDpi(IntPtr hwnd) => WindowInterop.GetDpi(hwnd);

    public static void BringToFront(IntPtr hwnd) => WindowInterop.BringToFront(hwnd);
}
