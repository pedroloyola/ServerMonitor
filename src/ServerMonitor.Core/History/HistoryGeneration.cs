namespace ServerMonitor.Core.History;

/// <summary>
/// A counter that moves forward every time local history is cleared or reset. Anything that started before the clear
/// (a remote probe still running, for example) captures <see cref="Current"/> when it starts and drops its outcome if
/// the value has moved on by the time it finishes, so nothing collected before a clear lands after it.
/// </summary>
public sealed class HistoryGeneration
{
    private long _current;

    /// <summary>The current generation. Starts at 0.</summary>
    public long Current => Interlocked.Read(ref _current);

    /// <summary>Raised synchronously by <see cref="Advance"/>, after the value moved. A handler that throws is ignored.</summary>
    public event EventHandler? Advanced;

    /// <summary>Moves to the next generation and returns it.</summary>
    public long Advance()
    {
        var next = Interlocked.Increment(ref _current);
        var handlers = Advanced;
        if (handlers is not null)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch
                {
                    // One listener must not stop the others from hearing about the clear.
                }
            }
        }

        return next;
    }
}
