using System.Runtime.Versioning;
using System.Threading;

namespace MergePool.Ui.Services;

/// <summary>
/// Keeps one window per signed-in user. A second launch — from the Start menu, or from sign-in
/// while the first is already in the notification area — hands the request to the instance that is
/// already running and exits, instead of opening a second window onto the same service.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\MergePool.Ui.Instance";

    private const string SignalName = @"Local\MergePool.Ui.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly CancellationTokenSource _shutdown = new();

    private SingleInstance(Mutex mutex, EventWaitHandle signal, bool isFirst)
    {
        _mutex = mutex;
        _signal = signal;
        IsFirst = isFirst;
    }

    /// <summary>False when another window is already running for this user.</summary>
    public bool IsFirst { get; }

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        return new SingleInstance(mutex, signal, isFirst);
    }

    /// <summary>Asks the running instance to bring its window up.</summary>
    public void SignalExistingInstance() => _signal.Set();

    /// <summary>Runs <paramref name="show"/> whenever another launch asks for the window.</summary>
    public void ListenForActivation(Action show)
    {
        ArgumentNullException.ThrowIfNull(show);

        var thread = new Thread(() =>
        {
            while (!_shutdown.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny([_signal, _shutdown.Token.WaitHandle]) == 0)
                {
                    show();
                }
            }
        })
        {
            IsBackground = true,
            Name = "MergePool activation listener",
        };

        thread.Start();
    }

    public void Dispose()
    {
        _shutdown.Cancel();

        if (IsFirst)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Already released, or never owned. Nothing to do.
            }
        }

        _mutex.Dispose();
        _signal.Dispose();
        _shutdown.Dispose();
    }
}
