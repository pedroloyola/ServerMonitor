namespace ServerMonitor.App.Services;

public readonly record struct RefreshAllResult(int Requested, int Succeeded, int Failed);

public interface IRefreshAllCoordinator
{
    Task<RefreshAllResult> RefreshAllAsync(CancellationToken cancellationToken = default);

    void BeginShutdown();

    Task StopAsync(CancellationToken cancellationToken = default);
}
