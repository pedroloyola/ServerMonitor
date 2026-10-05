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
