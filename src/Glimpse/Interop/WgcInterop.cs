using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace Glimpse.Interop;

/// <summary>
/// Windows.Graphics.Capture 的原生入口。
/// 通过 RoGetActivationFactory 取 <c>IGraphicsCaptureItemInterop</c>，
/// 绕开 CsWinRT 未公开的按显示器创建路径。
/// </summary>
internal static class WgcInterop
{
    private const string GraphicsCaptureItemClassId = "Windows.Graphics.Capture.GraphicsCaptureItem";

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr item);

        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr item);
    }

    /// <summary>为指定显示器创建捕获项。必须在显示器仍有效时调用。</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        int hr = WindowsCreateString(GraphicsCaptureItemClassId, GraphicsCaptureItemClassId.Length, out IntPtr classId);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            Guid interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            hr = RoGetActivationFactory(classId, ref interopIid, out IntPtr factoryPointer);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPointer);
                Guid itemIid = GraphicsCaptureItemIid;
                hr = interop.CreateForMonitor(monitor, ref itemIid, out IntPtr itemPointer);
                Marshal.ThrowExceptionForHR(hr);

                if (itemPointer == IntPtr.Zero)
                {
                    throw new InvalidOperationException("CreateForMonitor 返回空指针。");
                }

                try
                {
                    // 转成 CsWinRT 强类型对象后即可释放原始 ABI 引用
                    return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPointer);
                }
                finally
                {
                    Marshal.Release(itemPointer);
                }
            }
            finally
            {
                Marshal.Release(factoryPointer);
            }
        }
        finally
        {
            _ = WindowsDeleteString(classId);
        }
    }
}
