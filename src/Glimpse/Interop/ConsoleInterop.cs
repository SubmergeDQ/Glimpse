using System.Runtime.InteropServices;

namespace Glimpse.Interop;

/// <summary>控制台附加。WinExe 从终端启动时用于恢复标准输出。</summary>
internal static class ConsoleInterop
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", EntryPoint = "AttachConsole", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    public static bool AttachToParentProcess() => AttachConsole(AttachParentProcess);
}
