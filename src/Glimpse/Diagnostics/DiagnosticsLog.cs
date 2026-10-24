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

    /// <summary>
    /// 读取日志尾部若干行。以共享读打开，运行中也可调用；
    /// 只读取文件末尾一段，避免长稳测试后的大日志整份载入。
    /// </summary>
    public string[] ReadTail(int maxLines)
    {
        if (maxLines <= 0 || LogFilePath is null)
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(
                LogFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            long start = Math.Max(0, stream.Length - Math.Max(64 * 1024, maxLines * 160L));
            stream.Seek(start, SeekOrigin.Begin);

            using var reader = new StreamReader(stream);
            string[] lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // 从文件中段开始时首行可能被截断，丢弃它
            int skip = start > 0 && lines.Length > 0 ? 1 : 0;
            int count = Math.Min(maxLines, lines.Length - skip);
            var result = new string[count];
            for (int index = 0; index < count; index++)
            {
                result[index] = lines[lines.Length - count + index].TrimEnd('\r');
            }

            return result;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

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
