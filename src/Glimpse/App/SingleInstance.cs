namespace Glimpse.App;

/// <summary>
/// 单实例守卫。第二个实例直接退出，避免两个进程同时创建捕获会话与托盘。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Glimpse.SingleInstance";

    private readonly Mutex? _mutex;
    private bool _disposed;

    private SingleInstance(Mutex? mutex, bool isOwner)
    {
        _mutex = mutex;
        IsOwner = isOwner;
    }

    public bool IsOwner { get; }

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return new SingleInstance(null, false);
        }

        return new SingleInstance(mutex, true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (IsOwner && _mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 退出路径上重复释放时忽略
            }
        }

        _mutex?.Dispose();
    }
}
