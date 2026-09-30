using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Backup;

namespace ServerMonitor.Infrastructure.Persistence;

public sealed class JsonHostKeyTrustStore(
    HostKeyTrustStorageOptions storageOptions,
    ILogger<JsonHostKeyTrustStore> logger,
    IConfigurationWriteGate writeGate) : IHostKeyTrustStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<SshEndpoint, TrustedHostKey>? _entries;

    public async Task<TrustedHostKey?> GetAsync(
        SshEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var normalized = SshEndpoint.Create(endpoint.Host, endpoint.Port);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return _entries!.GetValueOrDefault(normalized);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TrustAsync(
        SshEndpoint endpoint,
        HostKeyIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(identity);
        var normalizedEndpoint = SshEndpoint.Create(endpoint.Host, endpoint.Port);
        var normalizedIdentity = HostKeyIdentity.Create(identity.Algorithm, identity.Sha256Fingerprint);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var lease = writeGate.EnterWrite();
            await EnsureLoadedAsync(cancellationToken);
            if (_entries!.TryGetValue(normalizedEndpoint, out var existing))
            {
                if (!existing.Identity.Matches(normalizedIdentity))
                {
                    throw new HostKeyTrustConflictException();
                }

                return;
            }

            _entries[normalizedEndpoint] = new TrustedHostKey
            {
                Endpoint = normalizedEndpoint,
                Identity = normalizedIdentity,
                ConfirmedAt = DateTimeOffset.UtcNow
            };
            await SaveAsync(cancellationToken);
            logger.LogInformation("Stored SSH host trust for {Host}:{Port}.", normalizedEndpoint.Host, normalizedEndpoint.Port);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(
        SshEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var normalized = SshEndpoint.Create(endpoint.Host, endpoint.Port);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var lease = writeGate.EnterWrite();
            await EnsureLoadedAsync(cancellationToken);
            if (!_entries!.Remove(normalized))
            {
                return false;
            }

            await SaveAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Export (M14.6 H-4, N2): the store's normalized, de-duplicated entries whose endpoint is in
    /// <paramref name="referenced"/>, and how many were left out. The WHOLE file is loaded and validated
    /// first: an invalid or unreadable file throws (<see cref="InvalidDataException"/>/<see cref="IOException"/>),
    /// so a backup never silently drops trust.
    /// </summary>
    internal async Task<(IReadOnlyList<TrustedHostKey> Entries, int Excluded)> ExportReferencedAsync(
        IReadOnlySet<SshEndpoint> referenced,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(referenced);
        var all = await ExportAllAsync(cancellationToken);
        var included = all.Where(entry => referenced.Contains(entry.Endpoint)).ToList();
        return (included, all.Count - included.Count);
    }

    /// <summary>Every entry, normalized, in file order (the store's own parser).</summary>
    internal async Task<IReadOnlyList<TrustedHostKey>> ExportAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return Order(_entries!.Values).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Restore-only replace (<paramref name="content"/> null deletes the file) under this store's lock;
    /// the cache is dropped so the next read loads the new file.</summary>
    internal async Task ReplaceForRestoreAsync(RestoreWriteToken token, byte[]? content, CancellationToken cancellationToken)
    {
        writeGate.EnsureHeldBy(token);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            DurableFile.Replace(storageOptions.FilePath, content);
            _entries = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The file bytes for <paramref name="entries"/>, exactly as a normal save writes them.</summary>
    internal static byte[] Render(IEnumerable<TrustedHostKey> entries) =>
        JsonSerializer.SerializeToUtf8Bytes(Order(entries), SerializerOptions);

    /// <summary>
    /// Parses a JSON array of entries with the given options (the backup reader passes its strict overlay) and
    /// applies the store's normalization and duplicate rule. Throws <see cref="InvalidDataException"/>.
    /// </summary>
    internal static IReadOnlyList<TrustedHostKey> ParseEntries(JsonElement array, JsonSerializerOptions options)
    {
        List<TrustedHostKey?>? persisted;
        try
        {
            persisted = array.Deserialize<List<TrustedHostKey?>>(options);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidDataException("The SSH host trust entries are invalid.", exception);
        }

        return Order(NormalizeEntries(persisted ?? throw new InvalidDataException("The SSH host trust entries are missing.")).Values).ToList();
    }

    private static IOrderedEnumerable<TrustedHostKey> Order(IEnumerable<TrustedHostKey> entries) =>
        entries.OrderBy(entry => entry.Endpoint.Host).ThenBy(entry => entry.Endpoint.Port);

    private static Dictionary<SshEndpoint, TrustedHostKey> NormalizeEntries(IEnumerable<TrustedHostKey?> persisted)
    {
        var loadedEntries = new Dictionary<SshEndpoint, TrustedHostKey>();
        foreach (var entry in persisted)
        {
            try
            {
                if (entry is null || entry.Endpoint is null || entry.Identity is null)
                {
                    throw new InvalidDataException("The SSH host trust entry is incomplete.");
                }

                var endpoint = SshEndpoint.Create(entry.Endpoint.Host, entry.Endpoint.Port);
                var identity = HostKeyIdentity.Create(
                    entry.Identity.Algorithm,
                    entry.Identity.Sha256Fingerprint);
                if (!loadedEntries.TryAdd(
                        endpoint,
                        entry with { Endpoint = endpoint, Identity = identity }))
                {
                    throw new InvalidDataException("The SSH host trust file contains a duplicate endpoint.");
                }
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException)
            {
                throw new InvalidDataException("The SSH host trust file contains an invalid entry.", exception);
            }
        }

        return loadedEntries;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_entries is not null)
        {
            return;
        }

        if (!File.Exists(storageOptions.FilePath))
        {
            _entries = [];
            return;
        }

        try
        {
            await using var stream = new FileStream(
                storageOptions.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                useAsync: true);
            var persisted = await JsonSerializer.DeserializeAsync<List<TrustedHostKey>>(
                stream,
                SerializerOptions,
                cancellationToken) ?? [];

            var loadedEntries = NormalizeEntries(persisted);
            _entries = loadedEntries;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            logger.LogWarning(
                "The SSH host trust file is invalid; SSH connections remain blocked. Exception type: {ExceptionType}.",
                exception.GetType().Name);
            throw new InvalidDataException("The SSH host trust file is invalid.", exception);
        }
        catch (IOException exception)
        {
            logger.LogWarning(
                "The SSH host trust file could not be read; SSH connections remain blocked. Exception type: {ExceptionType}.",
                exception.GetType().Name);
            throw;
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(storageOptions.FilePath)
            ?? throw new InvalidOperationException("The host trust storage path has no directory.");
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
                await stream.WriteAsync(Render(_entries!.Values), cancellationToken);
                await stream.FlushAsync(cancellationToken);
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
