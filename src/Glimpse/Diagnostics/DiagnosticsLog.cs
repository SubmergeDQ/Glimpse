using System.Diagnostics;

namespace Glimpse.Diagnostics;

/// <summary>
/// 诊断输出。有控制台时写控制台，同时始终追加到本地日志，
/// 便于双击启动（无控制台）时仍能事后核对指标。
/// </summary>
internal sealed class DiagnosticsLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter? _file;
    private bool _disposed;

    public DiagnosticsLog()
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Glimpse",
                "logs");
            Directory.CreateDirectory(directory);

            LogFilePath = Path.Combine(directory, "glimpse.log");

            // 共享读，便于运行中直接用编辑器查看
            var stream = new FileStream(
                LogFilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            _file = new StreamWriter(stream) { AutoFlush = true };
        }
        catch (IOException)
        {
            _file = null;
        }
        catch (UnauthorizedAccessException)
        {
            _file = null;
        }
    }

    public string? LogFilePath { get; }

    public void WriteLine(string message)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                Console.WriteLine(message);
            }
            catch (IOException)
            {
                // 无控制台时忽略，日志文件仍可查
            }

            Debug.WriteLine(message);

            try
            {
                _file?.WriteLine(message);
            }
            catch (IOException)
            {
                // 磁盘不可写时不影响主流程
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _file?.Dispose();
        }
    }
}
