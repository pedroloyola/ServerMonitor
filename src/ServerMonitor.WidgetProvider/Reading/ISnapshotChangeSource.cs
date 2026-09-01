namespace ServerMonitor.WidgetProvider.Reading;

/// <summary>
/// The filesystem seam under <see cref="WidgetSnapshotChangeWatcher"/>: something that reports "the
/// snapshot may have changed". Abstracting it keeps the whole pump lifecycle — arm/disarm, debounce,
/// coalescing, backstop, disposal — deterministically unit-testable on a <see cref="TimeProvider"/>,
/// while the real implementation (<see cref="FileSystemSnapshotChangeSource"/>) is covered separately by
/// tests that perform an ACTUAL atomic replace on disk.
/// <para>
/// The contract is deliberately coarse: a signal carries NO information about what changed. Every
/// consumer must simply re-read the file. That is the only correct behavior for a destination that is
/// replaced by rename rather than written in place, whose events may be duplicated (one commit produces
/// several) or lost outright (internal-buffer overflow).
/// </para>
/// </summary>
public interface ISnapshotChangeSource : IDisposable
{
    /// <summary>Raised when the snapshot may have changed. May fire spuriously; may be missed entirely.</summary>
    event Action? Changed;

    /// <summary>
    /// Begins (or re-establishes) watching. MUST NOT throw: a missing directory or an OS refusal leaves
    /// the source inert and reports it through <see cref="IsWatching"/>, so the caller's backstop can
    /// retry. Idempotent while a healthy watch is in place.
    /// </summary>
    void Start();

    /// <summary>Stops watching and releases OS resources. Idempotent.</summary>
    void Stop();

    /// <summary>
    /// False when no watch is currently established — either <see cref="Start"/> could not establish one,
    /// or the watch faulted and needs re-establishing. Retryable by the caller.
    /// </summary>
    bool IsWatching { get; }
}
