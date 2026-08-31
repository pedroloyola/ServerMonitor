namespace ServerMonitor.WidgetContract;

/// <summary>
/// Persists a <see cref="WidgetStateSnapshot"/> atomically so a reader never observes a half-written
/// file and a failed write preserves the last-known-good (§12/§13). Lives in the contract assembly so
/// the persistence layer can implement it without the App layer having to own the abstraction, and so
/// the writer side depends only on the wire contract — not on Core or the engine.
/// <para>
/// Implementations are single-writer and best-effort: the sole caller serializes writes, and a failure
/// is surfaced to that caller to isolate (never propagated into the monitoring cycle, §16).
/// </para>
/// </summary>
public interface IWidgetStateWriter
{
    Task WriteAsync(WidgetStateSnapshot snapshot, CancellationToken cancellationToken = default);
}
