using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.Infrastructure.Persistence;

/// <summary>
/// JSON-backed <see cref="IIgnoredDeviceStore"/>. Persists only non-sensitive stable identity
/// hashes. Unlike the SSH trust store, this is a UX convenience rather than a trust boundary,
/// so malformed or oversized input degrades to an empty set (with a warning) instead of
/// blocking anything — but it is still bounded: files larger than
/// <see cref="DiscoveryInputPolicy.MaxIgnoreFileBytes"/> are refused, and no more than
/// <see cref="DiscoveryInputPolicy.MaxIgnoredIdentities"/> entries are ever kept or written.
/// </summary>
public sealed class JsonIgnoredDeviceStore(
    IgnoredDeviceStorageOptions storageOptions,
    ILogger<JsonIgnoredDeviceStore> logger) : IIgnoredDeviceStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HashSet<string>? _entries;

    public async Task<IReadOnlySet<string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return new HashSet<string>(_entries!, StringComparer.Ordinal);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IgnoreAsync(string identityHash, CancellationToken cancellationToken = default)
    {
        // Reject anything that is not an exact lower-case SHA-256 hex hash before it is persisted.
        if (!DiscoveryInputPolicy.IsValidIdentityHash(identityHash))
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (_entries!.Contains(identityHash))
            {
                return true; // Already ignored and persisted.
            }

            if (_entries.Count >= DiscoveryInputPolicy.MaxIgnoredIdentities)
            {
                logger.LogWarning(
                    "Ignored-devices store is at capacity ({Max}); refusing to ignore a new device.",
                    DiscoveryInputPolicy.MaxIgnoredIdentities);
                return false;
            }

            // Persist a candidate snapshot first. The in-memory set represents committed state;
            // mutating it before the atomic replace succeeds would make a retry incorrectly look
            // persisted after an I/O failure.
            var candidate = new HashSet<string>(_entries, StringComparer.Ordinal)
            {
                identityHash
            };
            await SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
            _entries = candidate;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Always rewrite to a clean empty file, even if the loaded set is already empty: a
            // corrupt or oversize file is ignored on load, so only an unconditional save repairs it.
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var candidate = new HashSet<string>(StringComparer.Ordinal);
            await SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
            _entries = candidate;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_entries is not null)
        {
            return;
        }

        if (!File.Exists(storageOptions.FilePath))
        {
            _entries = new HashSet<string>(StringComparer.Ordinal);
            return;
        }

        try
        {
            var info = new FileInfo(storageOptions.FilePath);
            if (info.Length > DiscoveryInputPolicy.MaxIgnoreFileBytes)
            {
                logger.LogWarning(
                    "Ignored-devices file exceeds {Max} bytes; starting from an empty ignore set.",
                    DiscoveryInputPolicy.MaxIgnoreFileBytes);
                _entries = new HashSet<string>(StringComparer.Ordinal);
                return;
            }

            await using var stream = new FileStream(
                storageOptions.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                useAsync: true);
            var persisted = await JsonSerializer.DeserializeAsync<List<string>>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false) ?? [];

            var loaded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in persisted)
            {
                if (loaded.Count >= DiscoveryInputPolicy.MaxIgnoredIdentities)
                {
                    break;
                }

                // Only exact lower-case SHA-256 hex hashes are trusted; anything else is discarded.
                if (DiscoveryInputPolicy.IsValidIdentityHash(entry))
                {
                    loaded.Add(entry);
                }
            }

            _entries = loaded;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            logger.LogWarning(
                "Ignored-devices file could not be read ({Type}); starting from an empty ignore set.",
                exception.GetType().Name);
            _entries = new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private async Task SaveAsync(
        IReadOnlySet<string> entries,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(storageOptions.FilePath)
            ?? throw new InvalidOperationException("The ignored-devices storage path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryFile = storageOptions.FilePath + ".tmp";

        try
        {
            await using (var stream = new FileStream(
                temporaryFile,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    entries.Order(StringComparer.Ordinal),
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryFile, storageOptions.FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }
}
