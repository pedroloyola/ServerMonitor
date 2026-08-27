using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.History;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.App.Services;

/// <summary>
/// The single writer that owns all persistence (ADR-015 §1/§9; spec §27, §29, §30). It initializes
/// the store, drains the bounded channel in coalesced batches into atomic transactions, and runs
/// retention at startup and once a day — all off any UI thread. Registered as an
/// <see cref="IHostedService"/> <b>before</b> the monitoring engine so, on reverse-order shutdown, it
/// stops <i>after</i> the engine stops producing: it then drains what remains within a bound and lets
/// shutdown proceed regardless (history never delays process exit).
/// </summary>
public sealed class HistoryWriterService : IHostedService
{
    private const int MaxBatchSize = 256;
    private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan InitialRecoveryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRecoveryDelay = TimeSpan.FromMinutes(1);

    private readonly HistorySampleChannel _channel;
    private readonly IServerHistoryStore _store;
    private readonly HistoryStorageOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HistoryWriterService> _logger;

    private CancellationTokenSource? _cts;
    private Task _consumeTask = Task.CompletedTask;
    private Task _retentionTask = Task.CompletedTask;
    private Task _recoveryTask = Task.CompletedTask;

    public HistoryWriterService(
        HistorySampleChannel channel,
        IServerHistoryStore store,
        HistoryStorageOptions options,
        ILogger<HistoryWriterService> logger,
        TimeProvider? timeProvider = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Bounded, self-degrading initialization: a corrupt/locked database leaves the store
        // unavailable but never throws here, so app startup and monitoring proceed (spec §60).
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        _cts = new CancellationTokenSource();
        _consumeTask = Task.Run(() => ConsumeAsync(_cts.Token), CancellationToken.None);
        _retentionTask = Task.Run(() => RetentionLoopAsync(_cts.Token), CancellationToken.None);
        var firstRecoveryAttemptUtc = _timeProvider.GetUtcNow() + InitialRecoveryDelay;
        _recoveryTask = Task.Run(
            () => InitializationRecoveryLoopAsync(firstRecoveryAttemptUtc, _cts.Token),
            CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop accepting new samples, then let the consumer drain what remains within a bound.
        _channel.Complete();

        try
        {
            await _consumeTask.WaitAsync(DrainTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            _logger.LogDebug("History writer drain ended with {Reason}.", exception.GetType().Name);
        }

        _cts?.Cancel();
        _channel.FailPendingControls();

        var workers = Task.WhenAll(_consumeTask, _retentionTask, _recoveryTask);
        try
        {
            await workers
                .WaitAsync(TimeSpan.FromSeconds(2), _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            // Shutdown must proceed regardless; nothing else to do.
        }

        var cts = _cts;
        _cts = null;
        if (cts is not null)
        {
            if (workers.IsCompleted)
            {
                cts.Dispose();
            }
            else
            {
                _ = workers.ContinueWith(
                    static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                    cts,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    /// <summary>
    /// Clears through the same FIFO as writes. This is the ordering boundary used by Settings:
    /// everything accepted before the barrier is flushed and then deleted before success is reported.
    /// </summary>
    public Task<bool> ClearAsync(CancellationToken cancellationToken = default) =>
        _channel.RequestClearAsync(cancellationToken);

    public Task<bool> ResetAsync(CancellationToken cancellationToken = default) =>
        _channel.RequestResetAsync(cancellationToken);

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        var reader = _channel.Reader;
        var batch = new List<ServerHistorySample>(MaxBatchSize);
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                batch.Clear();
                while (reader.TryRead(out var item))
                {
                    if (item is HistorySampleItem sampleItem)
                    {
                        batch.Add(sampleItem.Sample);
                        if (batch.Count == MaxBatchSize)
                        {
                            await FlushAsync(batch, cancellationToken).ConfigureAwait(false);
                        }

                        continue;
                    }

                    if (item is HistoryClearItem clearItem)
                    {
                        await FlushAsync(batch, cancellationToken).ConfigureAwait(false);
                        bool cleared;
                        try
                        {
                            cleared = await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            clearItem.Completion.TrySetCanceled(cancellationToken);
                            throw;
                        }
                        catch (Exception exception)
                        {
                            _logger.LogError(
                                "History clear barrier failed unexpectedly. Type: {Type}.",
                                exception.GetType().Name);
                            cleared = false;
                        }

                        clearItem.Completion.TrySetResult(cleared);
                    }

                    if (item is HistoryResetItem resetItem)
                    {
                        await FlushAsync(batch, cancellationToken).ConfigureAwait(false);
                        bool reset;
                        try
                        {
                            reset = await _store.ResetAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            resetItem.Completion.TrySetCanceled(cancellationToken);
                            throw;
                        }
                        catch (Exception exception)
                        {
                            _logger.LogError(
                                "History reset barrier failed unexpectedly. Type: {Type}.",
                                exception.GetType().Name);
                            reset = false;
                        }

                        resetItem.Completion.TrySetResult(reset);
                    }
                }

                await FlushAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled shutdown after the bounded drain window.
        }
        catch (Exception exception)
        {
            _logger.LogError("History writer loop ended unexpectedly. Type: {Type}.", exception.GetType().Name);
        }
        finally
        {
            _channel.FailPendingControls();
        }
    }

    private async Task FlushAsync(List<ServerHistorySample> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await _store.WriteAsync(batch.ToArray(), cancellationToken).ConfigureAwait(false);
        batch.Clear();
    }

    private async Task RetentionLoopAsync(CancellationToken cancellationToken)
    {
        var nextRunUtc = _timeProvider.GetUtcNow();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = _timeProvider.GetUtcNow();
                if (now < nextRunUtc)
                {
                    await Task.Delay(nextRunUtc - now, _timeProvider, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Set the next absolute due time before invoking the store. A test or system clock
                // advance during the call is therefore observed by the next loop and cannot be lost.
                nextRunUtc = now + RetentionInterval;
                if (_store.IsAvailable && _options.RetentionPeriod > TimeSpan.Zero)
                {
                    var cutoff = now - _options.RetentionPeriod;
                    var removed = await _store.DeleteOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);
                    if (removed > 0)
                    {
                        _logger.LogInformation("History retention removed {Count} sample(s) older than {Cutoff:o}.", removed, cutoff);
                    }
                }

            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            _logger.LogError("History retention loop ended unexpectedly. Type: {Type}.", exception.GetType().Name);
        }
    }

    private async Task InitializationRecoveryLoopAsync(
        DateTimeOffset nextAttemptUtc,
        CancellationToken cancellationToken)
    {
        var delay = InitialRecoveryDelay;
        try
        {
            while (!_store.IsAvailable && _store.CanRetryInitialization)
            {
                var now = _timeProvider.GetUtcNow();
                if (now < nextAttemptUtc)
                {
                    await Task.Delay(nextAttemptUtc - now, _timeProvider, cancellationToken).ConfigureAwait(false);
                }

                await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaximumRecoveryDelay.Ticks));
                nextAttemptUtc = _timeProvider.GetUtcNow() + delay;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "History initialization recovery loop ended unexpectedly. Type: {Type}.",
                exception.GetType().Name);
        }
    }
}
