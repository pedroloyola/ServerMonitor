using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Services;

/// <summary>
/// Fans a manual refresh request out to all configured servers, including hidden ones.
/// Calls only the M6 monitoring facade, so its global concurrency limit and per-server
/// single-flight remain authoritative. Concurrent Refresh All requests share one batch.
/// </summary>
public sealed class RefreshAllCoordinator(
    IServerService serverService,
    IMonitoringEngine monitoringEngine,
    ILogger<RefreshAllCoordinator> logger) : IRefreshAllCoordinator, IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task<RefreshAllResult>? _currentBatch;
    private bool _stopping;
    private bool _disposed;

    public Task<RefreshAllResult> RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        Task<RefreshAllResult> batch;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping)
            {
                return Task.FromCanceled<RefreshAllResult>(new CancellationToken(canceled: true));
            }

            if (_currentBatch is null)
            {
                var completion = new TaskCompletionSource<RefreshAllResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _currentBatch = completion.Task;
                batch = completion.Task;
                _ = ExecuteAndCompleteAsync(completion);
            }
            else
            {
                batch = _currentBatch;
            }
        }

        return cancellationToken.CanBeCanceled
            ? batch.WaitAsync(cancellationToken)
            : batch;
    }

    public void BeginShutdown()
    {
        lock (_sync)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            _shutdownCts.Cancel();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        BeginShutdown();
        Task<RefreshAllResult>? batch;
        lock (_sync)
        {
            batch = _currentBatch;
        }

        if (batch is null)
        {
            return;
        }

        try
        {
            await batch.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested && batch.IsCompleted)
        {
            // Expected when application shutdown cancels a running batch.
        }
    }

    private async Task ExecuteAndCompleteAsync(TaskCompletionSource<RefreshAllResult> completion)
    {
        try
        {
            var servers = await serverService.GetAllAsync(_shutdownCts.Token).ConfigureAwait(false);
            var results = await Task.WhenAll(servers.Select(RefreshOneAsync)).ConfigureAwait(false);
            var succeeded = results.Count(result => result);
            completion.TrySetResult(new RefreshAllResult(servers.Count, succeeded, servers.Count - succeeded));
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            completion.TrySetCanceled(_shutdownCts.Token);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Refresh All could not enumerate configured servers.");
            completion.TrySetException(exception);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_currentBatch, completion.Task))
                {
                    _currentBatch = null;
                }
            }
        }
    }

    private async Task<bool> RefreshOneAsync(ServerMonitor.Core.Models.Server server)
    {
        try
        {
            var result = await monitoringEngine.RefreshNowAsync(server.Id, _shutdownCts.Token).ConfigureAwait(false);
            return result.IsSuccess;
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Refresh All failed for server {ServerId}; remaining servers will continue.",
                server.Id);
            return false;
        }
    }

    public void Dispose()
    {
        Task<RefreshAllResult>? batch;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            batch = _currentBatch;
        }

        BeginShutdown();
        if (batch is null || batch.IsCompleted)
        {
            _shutdownCts.Dispose();
            return;
        }

        // Host shutdown is bounded, so a dependency that ignores cancellation may outlive
        // StopAsync. Keep the CTS valid until that batch actually completes; disposing it
        // early can turn a late continuation into ObjectDisposedException. No new work can
        // enter after BeginShutdown.
        _ = batch.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            _shutdownCts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
