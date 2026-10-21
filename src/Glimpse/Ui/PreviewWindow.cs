using System.Runtime.InteropServices;
using Glimpse.Abstractions.Preview;

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

    private const int WmNcHitTest = 0x0084;
    private const int WmSizing = 0x0214;
    private const int HtTransparent = -1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private const int ResizeBorder = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private IPreviewHost? _host;
    private double _sourceAspect;

    public PreviewWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(160, 90);
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

    private void ConstrainSizing(SizingEdge edge, IntPtr rectPointer)
    {
        var rect = Marshal.PtrToStructure<NativeRect>(rectPointer);
        var bounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

        Rectangle adjusted = AspectSizing.Constrain(
            bounds,
            edge,
            _sourceAspect,
            MinimumSize.Width,
            MinimumSize.Height);

        rect.Left = adjusted.Left;
        rect.Top = adjusted.Top;
        rect.Right = adjusted.Right;
        rect.Bottom = adjusted.Bottom;
        Marshal.StructureToPtr(rect, rectPointer, false);
    }

    private IntPtr HitTest(IntPtr screenPoint)
    {
        if (_host?.IsClickThrough == true)
        {
            return new IntPtr(HtTransparent);
        }

        // lParam 的低 16 位是屏幕 X，高 16 位是屏幕 Y（带符号）
        int screenX = unchecked((short)(long)screenPoint);
        int screenY = unchecked((short)((long)screenPoint >> 16));
        Point point = PointToClient(new Point(screenX, screenY));

        int width = ClientSize.Width;
        int height = ClientSize.Height;

        bool left = point.X < ResizeBorder;
        bool right = point.X >= width - ResizeBorder;
        bool top = point.Y < ResizeBorder;
        bool bottom = point.Y >= height - ResizeBorder;

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
