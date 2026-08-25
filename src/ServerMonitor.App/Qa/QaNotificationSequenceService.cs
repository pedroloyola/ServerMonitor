using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Qa;

/// <summary>
/// Emits a small fixed sequence after the real coordinator has established its Healthy
/// baseline: Warning, Critical, Healthy, Offline, Healthy. No network or persistence exists.
/// </summary>
internal sealed class QaNotificationSequenceService(
    IServerMonitoringStateStore stateStore,
    ILogger<QaNotificationSequenceService> logger) : IHostedService
{
    internal static readonly IReadOnlyList<ServerHealth> Sequence =
        [ServerHealth.Warning, ServerHealth.Critical, ServerHealth.Healthy, ServerHealth.Offline, ServerHealth.Healthy];

    private CancellationTokenSource? _lifetime;
    private Task? _sequenceTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_sequenceTask is not null)
        {
            return Task.CompletedTask;
        }

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sequenceTask = RunAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _lifetime?.Cancel();
        if (_sequenceTask is null)
        {
            return;
        }

        try
        {
            await _sequenceTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal harness shutdown.
        }
        finally
        {
            _lifetime?.Dispose();
            _lifetime = null;
            _sequenceTask = null;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        foreach (var health in Sequence)
        {
            var previous = stateStore.Get(QaNotificationServerService.ServerId);
            var now = DateTimeOffset.UtcNow;
            stateStore.Set(previous with
            {
                Health = health,
                LastAttemptAt = now,
                LastSuccessAt = health == ServerHealth.Offline ? previous.LastSuccessAt : now
            });
            logger.LogDebug("QA notification transition emitted: {Health}.", health);
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
    }
}
