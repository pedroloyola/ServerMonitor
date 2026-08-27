using ServerMonitor.Core.Models;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.Core.Interfaces;

/// <summary>
/// Collects read-only workload observability (Docker containers + managed services) for one server,
/// analogous to <see cref="IServerMetricsCollector"/> but for the M11 workload domain. The UI never
/// receives this directly; the App's collector service drives it off the engine thread.
/// <para>
/// The returned <see cref="ServerWorkloadSnapshot"/> describes a single fresh attempt: Docker and
/// Services each carry their own availability, so a Docker failure and a services failure are
/// independent (§38), and a total SSH failure yields both views <c>Unknown</c>. Implementations must
/// not throw for expected remote failures — they encode them as availabilities. Freshness carry-over
/// on failure (keeping the previous lists, marking stale) is the caller's responsibility, not the
/// collector's, so implementations return <see cref="ServerWorkloadSnapshot.IsStale"/> = <c>false</c>
/// with <see cref="ServerWorkloadSnapshot.CapturedAtUtc"/> = the attempt time.
/// </para>
/// </summary>
public interface IWorkloadCollector
{
    Task<ServerWorkloadSnapshot> CollectAsync(Server server, CancellationToken cancellationToken = default);
}
