using System.Collections.Concurrent;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// The app's UI thread for a test: one dedicated thread running a FIFO queue, installed as its
/// <see cref="SynchronizationContext"/> - what the WinUI dispatcher is to the production navigation. A scenario run
/// through <see cref="RunAsync"/> captures this context in every <c>await</c> (test and production), so continuations
/// run on the owner thread in posting order instead of on whichever pool thread a previous await resumed on (xUnit's
/// own context only posts to the pool). <see cref="DrainAsync"/> runs everything already posted - "the dispatcher got
/// to it" - before the next gesture, without any timing.
/// </summary>
internal sealed class OwnerThread : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly Thread _thread;

    public OwnerThread()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "test-owner-thread" };
        _thread.Start();
    }

    public bool IsCurrent => Environment.CurrentManagedThreadId == _thread.ManagedThreadId && Current == this;

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("The owner thread is only posted to, like a dispatcher.");

    /// <summary>Runs the scenario on the owner thread and completes with it (its exceptions included).</summary>
    public Task RunAsync(Func<Task> scenario)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try
            {
                await scenario();
                done.SetResult();
            }
            catch (Exception exception)
            {
                done.SetException(exception);
            }
        }, null);
        return done.Task;
    }

    /// <summary>
    /// Called on the owner thread: returns after every callback posted before this call has run (FIFO: the marker is
    /// queued behind them, and the awaiting continuation is posted behind the marker).
    /// </summary>
    public Task DrainAsync()
    {
        if (!IsCurrent)
        {
            throw new InvalidOperationException("DrainAsync must be called on the owner thread.");
        }

        var marker = new TaskCompletionSource();
        Post(_ => marker.SetResult(), null);
        return marker.Task;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
        {
            _thread.Join();
        }
    }

    private void Pump()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            callback(state);
        }
    }
}
