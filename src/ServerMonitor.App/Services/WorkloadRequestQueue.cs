using System.Threading.Channels;

namespace ServerMonitor.App.Services;

/// <summary>
/// Bounded queue between the (non-blocking) cadence observer producer and the single
/// <see cref="WorkloadCollectorService"/> consumer, for <b>scheduled</b> requests only. Bounded so a
/// stalled collector can never grow memory without limit: when full, <see cref="TryEnqueueScheduled"/>
/// returns <c>false</c> and the observer drops the request observably (an honest gap) — it never blocks
/// the engine thread. Manual refreshes bypass this queue entirely. Mirrors <c>HistorySampleChannel</c>.
/// </summary>
public sealed class WorkloadRequestQueue
{
    private readonly Channel<WorkloadRequest> _channel;

    public WorkloadRequestQueue(WorkloadOptions? options = null)
    {
        var capacity = (options ?? WorkloadOptions.Default).QueueCapacity;
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Queue capacity must be positive.");
        }

        _channel = Channel.CreateBounded<WorkloadRequest>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    /// <summary>Non-blocking enqueue for scheduled requests. Returns <c>false</c> when full (caller drops).</summary>
    public bool TryEnqueueScheduled(WorkloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _channel.Writer.TryWrite(request);
    }

    internal ChannelReader<WorkloadRequest> Reader => _channel.Reader;

    /// <summary>Stops accepting new requests so the consumer can drain and exit at shutdown.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
