namespace Glimpse.App;

/// <summary>
/// 单实例守卫。第二个实例不重复启动，而是通知已有实例显示预览窗后退出。
/// 通知使用命名事件：HWND_BROADCAST 不会投递到 WS_EX_TOOLWINDOW 窗口。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Glimpse.SingleInstance";
    private const string ActivationEventName = @"Local\Glimpse.Activate";

    private readonly Mutex? _mutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;
    private Thread? _activationThread;
    private bool _disposed;

    private SingleInstance(Mutex? mutex, bool isOwner)
    {
        _mutex = mutex;
        IsOwner = isOwner;
    }

    public bool IsOwner { get; }

    /// <summary>由已有实例调用，在后台等待第二个实例的激活请求。</summary>
    public void ListenForActivation(Action onActivate)
    {
        if (!IsOwner || _disposed)
        {
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationCancellation = new CancellationTokenSource();
        CancellationToken token = _activationCancellation.Token;

        _activationThread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                if (_activationEvent.WaitOne(500))
                {
                    onActivate();
                }
            }
        })
        {
            IsBackground = true,
            Name = "Glimpse.Activation",
        };
        _activationThread.Start();
    }

    /// <summary>第二个实例调用：唤醒已有实例。</summary>
    public static void RequestActivation()
    {
        try
        {
            using EventWaitHandle activation = EventWaitHandle.OpenExisting(ActivationEventName);
            activation.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // 已有实例还没创建监听，忽略
        }
    }

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

        _activationCancellation?.Cancel();
        _activationEvent?.Set();
        _activationThread?.Join(1000);
        _activationCancellation?.Dispose();
        _activationEvent?.Dispose();

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
