namespace ServerMonitor.Core.Workloads;

/// <summary>
/// Transient, in-memory store of the latest <see cref="ServerWorkloadSnapshot"/> per server (§40).
/// The workload collector service writes it; the UI observes <see cref="WorkloadChanged"/> to render
/// the Docker/Services section. Never persisted; holds no secrets. Mirrors the shape of the M6
/// monitoring state store.
/// </summary>
public interface IServerWorkloadStore
{
    event EventHandler<Guid>? WorkloadChanged;

    /// <summary>The latest snapshot for a server, or <c>null</c> if none has been collected yet.</summary>
    ServerWorkloadSnapshot? Get(Guid serverId);

    IReadOnlyCollection<ServerWorkloadSnapshot> GetAll();

    void Set(ServerWorkloadSnapshot snapshot);

    void Remove(Guid serverId);
}
