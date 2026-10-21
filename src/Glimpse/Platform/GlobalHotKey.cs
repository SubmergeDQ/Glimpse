using System.ComponentModel;
using System.Runtime.InteropServices;
using Glimpse.Interop;

namespace Glimpse.Platform;

/// <summary>
/// 全局热键。使用独立的 message-only 窗口接收 WM_HOTKEY，
/// 不依赖主窗口是否拥有前台焦点。
/// </summary>
internal sealed class GlobalHotKey : NativeWindow, IDisposable
{
    /// <summary>Ctrl+Alt 组合，供组合根直接使用，避免 UI/App 层散落原生常量。</summary>
    public const uint ControlAlt = WindowInterop.ModControl | WindowInterop.ModAlt;

    /// <summary>Ctrl+Shift 备选组合，用于 Ctrl+Alt+P 被其他软件占用的情况。</summary>
    public const uint ControlShift = WindowInterop.ModControl | WindowInterop.ModShift;

    private const int WmHotKey = 0x0312;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly int _id;
    private bool _registered;
    private bool _disposed;

    public GlobalHotKey(int id, uint modifiers, uint virtualKey)
    {
        _id = id;

        CreateHandle(new CreateParams
        {
            Caption = $"GlimpseHotKey_{id}",
            ClassName = "STATIC",
            Parent = HwndMessage,
        });

        if (!WindowInterop.RegisterHotKey(Handle, id, modifiers | WindowInterop.ModNoRepeat, virtualKey))
        {
            int error = Marshal.GetLastWin32Error();
            DestroyHandle();
            throw new Win32Exception(error, $"注册全局热键失败（Win32 错误 {error}）。");
        }

        _registered = true;
    }

    public event Action? Pressed;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotKey && m.WParam == new IntPtr(_id))
        {
            Pressed?.Invoke();
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_registered)
        {
            WindowInterop.UnregisterHotKey(Handle, _id);
            _registered = false;
        }

        DestroyHandle();
    }
}
