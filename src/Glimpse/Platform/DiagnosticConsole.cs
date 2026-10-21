using System.Text;
using Glimpse.Interop;

namespace Glimpse.Platform;

/// <summary>
/// WinExe 默认没有控制台。从终端启动时附加到父进程控制台，
/// 双击启动则保持无窗口，统计仍会写入日志文件。
/// </summary>
internal static class DiagnosticConsole
{
    public static void AttachToParent()
    {
        if (!ConsoleInterop.AttachToParentProcess())
        {
            return;
        }

        try
        {
            var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(output);

            var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetError(error);

            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception)
        {
            // 控制台句柄不可用时静默回退到日志文件，诊断输出不得阻断启动
        }
    }
}
