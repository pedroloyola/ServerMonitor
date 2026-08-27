using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY deterministic <see cref="IWorkloadCollector"/>: serves the catalog snapshot for a server
/// without any SSH, Docker host or service manager. Returns it as a fresh attempt (the harness owns
/// freshness by pre-populating the store), so a manual refresh re-serves the same shape. Unknown ids get
/// an all-<c>Unknown</c> snapshot. Excluded from Release.
/// </summary>
internal sealed class QaWorkloadCollector : IWorkloadCollector
{
    public Task<ServerWorkloadSnapshot> CollectAsync(Server server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        var workload = QaWorkloadsCatalog.WorkloadFor(server.Id)
            ?? ServerWorkloadSnapshot.Initial(server.Id, QaWorkloadsCatalog.Now);
        return Task.FromResult(workload);
    }
}
