namespace ServerMonitor.Core.Domain;

/// <summary>
/// A multi-file server save failed part-way AND the files already written could not be restored, so what
/// is on disk may be the new state, the old state, or a mix. Callers must then assume the new configuration
/// MAY be persisted: keep any secret it could reference (a possible orphan is acceptable; a server pointing
/// at a deleted secret is not). Derives from <see cref="IOException"/> so existing I/O handling still applies.
/// </summary>
public sealed class ServerPersistencePartialCommitException(string message, Exception innerException)
    : IOException(message, innerException);
