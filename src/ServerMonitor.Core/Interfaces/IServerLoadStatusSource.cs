namespace ServerMonitor.Core.Interfaces;

public enum ServerLoadStatus
{
    NotFound,
    Loaded,
    Unavailable
}

/// <summary>Additive, read-only diagnosis. It does not change the legacy list or write contracts.</summary>
public interface IServerLoadStatusSource
{
    Task<ServerLoadStatus> GetLoadStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>Read-only detail used to reconcile a diagnostic read with the process-cached list.</summary>
public sealed record ServerLoadDiagnosis(ServerLoadStatus Status, int LoadableCount);

public interface IServerLoadDiagnosisSource : IServerLoadStatusSource
{
    Task<ServerLoadDiagnosis> GetLoadDiagnosisAsync(CancellationToken cancellationToken = default);
}
