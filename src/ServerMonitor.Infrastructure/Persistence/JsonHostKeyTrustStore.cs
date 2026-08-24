using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;

namespace ServerMonitor.Infrastructure.Persistence;

public sealed class JsonHostKeyTrustStore(
    HostKeyTrustStorageOptions storageOptions,
    ILogger<JsonHostKeyTrustStore> logger) : IHostKeyTrustStore, IDisposable
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

        var loadedEntries = new Dictionary<SshEndpoint, TrustedHostKey>();
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
                await JsonSerializer.SerializeAsync(
                    stream,
                    _entries!.Values.OrderBy(entry => entry.Endpoint.Host).ThenBy(entry => entry.Endpoint.Port),
                    SerializerOptions,
                    cancellationToken);
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
