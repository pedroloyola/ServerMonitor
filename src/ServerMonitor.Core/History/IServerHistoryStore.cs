namespace ServerMonitor.Core.History;

/// <summary>
/// Low-level persistence for server history (implemented over SQLite in Infrastructure). All members
/// are degradable: when the store is unavailable (corrupt/failed to open), writes and queries fail
/// softly and <see cref="IsAvailable"/> is <c>false</c> — monitoring never depends on this
/// (ADR-015 §1, §9). Every query is parameterized; the store contains metrics only, never secrets.
/// </summary>
public interface IServerHistoryStore
{
    /// <summary>False when the database could not be opened/migrated (e.g. corruption). The UI then
    /// shows "history unavailable" and offers an explicit reset; the app keeps running.</summary>
    bool IsAvailable { get; }

    /// <summary>True only when initialization failed for a transient SQLite condition (busy/locked)
    /// and a bounded-backoff retry may recover without destructive reset.</summary>
    bool CanRetryInitialization { get; }

    /// <summary>Opens the database and applies migrations. Never throws for a corrupt/locked file:
    /// it logs, leaves <see cref="IsAvailable"/> false, and returns.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists a batch atomically (single transaction, <c>INSERT OR IGNORE</c> for
    /// idempotency on duplicate server+timestamp). A no-op when unavailable.</summary>
    Task WriteAsync(IReadOnlyList<ServerHistorySample> batch, CancellationToken cancellationToken = default);

    /// <summary>Returns samples for one server within [start,end] UTC, ascending by time. Empty when
    /// unavailable or none found. Never leaks other servers' rows.</summary>
    Task<IReadOnlyList<ServerHistorySample>> QueryAsync(
        Guid serverId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes samples older than the cutoff (retention). Returns rows removed.</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default);

    /// <summary>Removes all history rows (Clear history). Destructive; touches only history data.
    /// Returns <c>true</c> only when the delete completed successfully.</summary>
    Task<bool> ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>Recreates the database file from scratch — the explicit recovery path for a corrupt
    /// database (never invoked automatically). Restores <see cref="IsAvailable"/> on success.</summary>
    Task<bool> ResetAsync(CancellationToken cancellationToken = default);
}
