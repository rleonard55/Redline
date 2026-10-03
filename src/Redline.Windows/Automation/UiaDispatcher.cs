using System.Collections.Concurrent;

namespace Redline.Windows.Automation;

/// <summary>
/// Provides a dedicated single-threaded apartment (STA) thread for executing
/// UI Automation COM calls safely without deadlocks or marshaling corruption.
/// </summary>
public sealed class UiaDispatcher : IDisposable
{
    private readonly Thread _staThread;
    private readonly BlockingCollection<Action> _workQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public UiaDispatcher()
    {
        _staThread = new Thread(RunMessagePump)
        {
            Name = "Redline.UiaDispatcher",
            IsBackground = true
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _workQueue.Add(() =>
            {
                if (_cts.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(_cts.Token);
                    return;
                }

                try
                {
                    var result = func();
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            tcs.TrySetCanceled(_cts.Token);
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(UiaDispatcher)));
        }

        return tcs.Task;
    }

    public Task InvokeAsync(Action action)
    {
        return InvokeAsync<object?>(() =>
        {
            action();
            return null;
        });
    }

    private void RunMessagePump()
    {
        try
        {
            foreach (var workItem in _workQueue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    workItem();
                }
                catch
                {
                    // Unhandled exceptions inside work items are captured by their TaskCompletionSource.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _workQueue.CompleteAdding();

        if (_staThread.IsAlive && Thread.CurrentThread != _staThread)
        {
            _staThread.Join(500);
        }

        _workQueue.Dispose();
        _cts.Dispose();
    }
}
