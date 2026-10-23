using System.Runtime.InteropServices;
using Glimpse.Abstractions.Preview;
using Glimpse.Platform;

namespace Glimpse.Ui;

/// <summary>
/// 预览窗外壳：无边框、置顶、不抢焦点、不进 Alt+Tab。
/// 带 WS_EX_NOREDIRECTIONBITMAP 的窗口没有 DWM 重定向表面，
/// 因此这里不能放任何 WinForms 控件，也不能产生 GDI 绘制。
/// </summary>
internal sealed class PreviewWindow : Form
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoRedirectionBitmap = 0x00200000;
    private const long WsExNoActivate = 0x08000000;

    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcHitTest = 0x0084;
    private const int WmDisplayChange = 0x007E;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int WmDpiChanged = 0x02E0;
    private const int WmSizing = 0x0214;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    // 以下三个常量按 96 DPI 的逻辑像素书写，使用前经 DpiScaling 换算
    private const int ResizeBorder = 6;
    private const int MinimumLogicalWidth = 160;
    private const int MinimumLogicalHeight = 90;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    private IPreviewHost? _host;
    private double _sourceAspect;
    private int _dpi;
    private bool _inSizeMove;

    public PreviewWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        MaximizeBox = false;
        MinimizeBox = false;
        Text = "Glimpse";

        // 位置和尺寸全部由 SetWindowPos 以物理像素指定，禁止 WinForms 再按 DPI 缩放
        AutoScaleMode = AutoScaleMode.None;

        // 所有可见像素来自交换链，WinForms 不做任何背景绘制
        SetStyle(ControlStyles.Opaque, true);
        SetStyle(ControlStyles.UserPaint, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.Style = WsPopup;
            parameters.ExStyle |= unchecked((int)(WsExToolWindow | WsExNoRedirectionBitmap | WsExNoActivate));
            return parameters;
        }
    }

    public void AttachHost(IPreviewHost host) => _host = host;

    /// <summary>系统显示配置变化（插拔、分辨率或缩放调整），由组合根重新枚举并收敛。</summary>
    public event Action? DisplayChanged;

    private int CurrentDpi => _dpi > 0 ? _dpi : DpiScaling.DefaultDpi;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _dpi = WindowPlacement.GetDpi(Handle);
    }

    private (int Width, int Height) MinimumPhysicalSize() => (
        DpiScaling.Scale(MinimumLogicalWidth, CurrentDpi),
        DpiScaling.Scale(MinimumLogicalHeight, CurrentDpi));

    /// <summary>设置源画面尺寸，交互缩放时按该宽高比约束窗口。</summary>
    public void SetSourceSize(int width, int height)
    {
        if (width > 0 && height > 0)
        {
            _sourceAspect = width / (double)height;
        }
    }

    // 没有重定向表面，绘制不会显示，反而可能产生黑块
    protected override void OnPaint(PaintEventArgs e)
    {
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);

        if (WindowState == FormWindowState.Normal)
        {
            _host?.Resize(ClientSize.Width, ClientSize.Height);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmDisplayChange)
        {
            DisplayChanged?.Invoke();
            base.WndProc(ref m);
            return;
        }

        if (m.Msg == WmGetMinMaxInfo)
        {
            // WinForms 按 MinimumSize 写入的仍是固定物理像素，这里按当前 DPI 覆盖
            base.WndProc(ref m);
            var info = Marshal.PtrToStructure<MinMaxInfo>(m.LParam);
            (int minWidth, int minHeight) = MinimumPhysicalSize();
            info.MinTrackSize.X = Math.Max(info.MinTrackSize.X, minWidth);
            info.MinTrackSize.Y = Math.Max(info.MinTrackSize.Y, minHeight);
            Marshal.StructureToPtr(info, m.LParam, false);
            return;
        }

        if (m.Msg == WmEnterSizeMove)
        {
            _inSizeMove = true;
            base.WndProc(ref m);
            return;
        }

        if (m.Msg == WmExitSizeMove)
        {
            _inSizeMove = false;
            base.WndProc(ref m);
            return;
        }

        if (m.Msg == WmDpiChanged)
        {
            HandleDpiChanged(m.WParam.ToInt32(), m.LParam);
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == WmSizing
            && _sourceAspect > 0
            && ToSizingEdge(m.WParam.ToInt32()) is { } edge)
        {
            ConstrainSizing(edge, m.LParam);
            m.Result = new IntPtr(1);
            return;
        }

        if (m.Msg == WmNcHitTest)
        {
            m.Result = HitTest(m.LParam);
            return;
        }

        base.WndProc(ref m);
    }

    private static SizingEdge? ToSizingEdge(int value) => value switch
    {
        1 => SizingEdge.Left,
        2 => SizingEdge.Right,
        3 => SizingEdge.Top,
        4 => SizingEdge.TopLeft,
        5 => SizingEdge.TopRight,
        6 => SizingEdge.Bottom,
        7 => SizingEdge.BottomLeft,
        8 => SizingEdge.BottomRight,
        _ => null,
    };

    /// <summary>
    /// 跨屏 DPI 变化。用户拖动窗口跨到不同缩放的显示器时，系统给出的建议矩形
    /// 按新 DPI 等比重算物理尺寸；这里再补齐宽高比与最小尺寸。
    /// 程序侧用 SetWindowPos 显式定位时保持调用方给定的物理尺寸，避免二次缩放。
    /// </summary>
    private void HandleDpiChanged(int wParam, IntPtr suggestedRectPointer)
    {
        int newDpi = wParam & 0xFFFF;
        if (newDpi > 0)
        {
            _dpi = newDpi;
        }

        if (!_inSizeMove || suggestedRectPointer == IntPtr.Zero)
        {
            return;
        }

        var rect = Marshal.PtrToStructure<NativeRect>(suggestedRectPointer);
        var bounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        (int minWidth, int minHeight) = MinimumPhysicalSize();

        Rectangle adjusted = AspectSizing.Constrain(
            bounds,
            SizingEdge.Right,
            _sourceAspect,
            minWidth,
            minHeight);

        WindowPlacement.Place(Handle, adjusted.Left, adjusted.Top, adjusted.Width, adjusted.Height);
    }

    private void ConstrainSizing(SizingEdge edge, IntPtr rectPointer)
    {
        var rect = Marshal.PtrToStructure<NativeRect>(rectPointer);
        var bounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        (int minWidth, int minHeight) = MinimumPhysicalSize();

        Rectangle adjusted = AspectSizing.Constrain(
            bounds,
            edge,
            _sourceAspect,
            minWidth,
            minHeight);

        rect.Left = adjusted.Left;
        rect.Top = adjusted.Top;
        rect.Right = adjusted.Right;
        rect.Bottom = adjusted.Bottom;
        Marshal.StructureToPtr(rect, rectPointer, false);
    }

    private IntPtr HitTest(IntPtr screenPoint)
    {
        // 穿透状态完全交给 WS_EX_TRANSPARENT：HTTRANSPARENT 只对同线程窗口有效，
        // 下层窗口属于其他进程时返回它会把点击丢掉，反而使穿透失效
        int screenX = unchecked((short)(long)screenPoint);
        int screenY = unchecked((short)((long)screenPoint >> 16));
        Point point = PointToClient(new Point(screenX, screenY));

        int width = ClientSize.Width;
        int height = ClientSize.Height;
        int border = DpiScaling.Scale(ResizeBorder, CurrentDpi);

        bool left = point.X < border;
        bool right = point.X >= width - border;
        bool top = point.Y < border;
        bool bottom = point.Y >= height - border;

        if (top && left)
        {
            return new IntPtr(HtTopLeft);
        }

        if (top && right)
        {
            return new IntPtr(HtTopRight);
        }

        if (bottom && left)
        {
            return new IntPtr(HtBottomLeft);
        }

        if (bottom && right)
        {
            return new IntPtr(HtBottomRight);
        }

        if (left)
        {
            return new IntPtr(HtLeft);
        }

        if (right)
        {
            return new IntPtr(HtRight);
        }

        if (top)
        {
            return new IntPtr(HtTop);
        }

        if (bottom)
        {
            return new IntPtr(HtBottom);
        }

        // 内部按标题栏处理，实现无边框拖动；WS_EX_NOACTIVATE 保证不抢焦点
        return new IntPtr(HtCaption);
    }
}
