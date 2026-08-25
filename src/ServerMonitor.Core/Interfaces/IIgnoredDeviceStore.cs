namespace ServerMonitor.Core.Interfaces;

/// <summary>
/// Persists the user's "ignored" decisions for discovered devices, keyed by the non-sensitive
/// stable hash of a service instance identity — stored separately from <c>servers.json</c> and
/// never containing hostnames, addresses, credentials or trust material. Ignoring a suggestion
/// hides it; resetting clears the whole set so still-present devices can be suggested again.
/// </summary>
public interface IIgnoredDeviceStore
{
    /// <summary>Loads the current set of ignored identity hashes. Corrupt/oversize input yields an empty set.</summary>
    Task<IReadOnlySet<string>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an identity hash as ignored and reports whether it is now persisted. Returns
    /// <c>true</c> when the hash is stored (newly added or already present), <c>false</c> when it
    /// is refused — an invalid hash, or the store is at capacity. A <c>false</c> result means the
    /// caller must not treat the device as ignored, not even for the current session.
    /// </summary>
    Task<bool> IgnoreAsync(string identityHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears every ignored identity and repairs the backing store, rewriting it to a clean empty
    /// state even when the currently loaded set is already empty (e.g. the file was corrupt or
    /// oversize and had been ignored on load).
    /// </summary>
    Task ResetAsync(CancellationToken cancellationToken = default);
}
