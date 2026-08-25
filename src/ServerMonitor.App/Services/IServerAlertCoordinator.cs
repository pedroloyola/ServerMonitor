using Microsoft.Extensions.Hosting;

namespace ServerMonitor.App.Services;

/// <summary>Observes M6 state transitions and coordinates M8 user alerts.</summary>
public interface IServerAlertCoordinator : IHostedService
{
    /// <summary>Synchronously fences new alert work before the async host shutdown drain.</summary>
    void BeginShutdown();
}
