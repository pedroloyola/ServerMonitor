using System.Threading.Channels;
using System.Collections.Concurrent;
using ServerMonitor.Core.History;

namespace ServerMonitor.App.Services;

/// <summary>
/// The bounded queue between the (non-blocking) <see cref="HistoryRecorder"/> producer and the single
/// <see cref="HistoryWriterService"/> consumer (ADR-015 §1; spec §27, §28). Bounded so a stalled
/// database can never grow memory without limit: when full, <see cref="TryWrite"/> returns
/// <c>false</c> and the producer drops the new sample observably — it never blocks the monitoring
/// loop.
/// </summary>
public sealed class HistorySampleChannel
{
    public const int DefaultCapacity = 1024;

    private readonly Channel<HistoryQueueItem> _channel;
    private readonly ConcurrentDictionary<TaskCompletionSource<bool>, byte> _pendingControls = new();

    public HistorySampleChannel(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _channel = Channel.CreateBounded<HistoryQueueItem>(new BoundedChannelOptions(capacity)
        {
            // Wait mode + TryWrite gives an observable "full" signal (TryWrite == false) without ever
            // blocking the writer thread. Multiple server loops produce; one writer service consumes.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    /// <summary>Non-blocking enqueue. Returns <c>false</c> when the queue is full (caller drops).</summary>
    public bool TryWrite(ServerHistorySample sample) =>
        _channel.Writer.TryWrite(new HistorySampleItem(sample));

    internal ChannelReader<HistoryQueueItem> Reader => _channel.Reader;

    /// <summary>
    /// Enqueues an ordered clear barrier. Samples already accepted are persisted before the delete;
    /// samples accepted afterwards remain new history. The result is true only after the store
    /// confirms the delete, so the UI can never announce a false success.
    /// </summary>
    internal async Task<bool> RequestClearAsync(CancellationToken cancellationToken)
        => await RequestControlAsync(
            static completion => new HistoryClearItem(completion),
            cancellationToken).ConfigureAwait(false);

    internal async Task<bool> RequestResetAsync(CancellationToken cancellationToken)
        => await RequestControlAsync(
            static completion => new HistoryResetItem(completion),
            cancellationToken).ConfigureAwait(false);

    private async Task<bool> RequestControlAsync(
        Func<TaskCompletionSource<bool>, HistoryQueueItem> createItem,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingControls.TryAdd(completion, 0);
        try
        {
            await _channel.Writer
                .WriteAsync(createItem(completion), cancellationToken)
                .ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return false;
        }
        finally
        {
            _pendingControls.TryRemove(completion, out _);
        }
    }

    internal void FailPendingControls()
    {
        foreach (var completion in _pendingControls.Keys)
        {
            completion.TrySetResult(false);
        }
    }

    /// <summary>Stops accepting new samples so the consumer can drain and exit at shutdown.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}

internal abstract record HistoryQueueItem;

internal sealed record HistorySampleItem(ServerHistorySample Sample) : HistoryQueueItem;

internal sealed record HistoryClearItem(TaskCompletionSource<bool> Completion) : HistoryQueueItem;

internal sealed record HistoryResetItem(TaskCompletionSource<bool> Completion) : HistoryQueueItem;
