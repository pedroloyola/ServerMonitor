using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;

namespace ServerMonitor.Infrastructure.Persistence;

/// <summary>
/// Trust for targets reached through a jump host, in <c>known-hosts.routes.json</c>:
/// <c>{ "schemaVersion": 1, "entries": [ { "via", "endpoint", "identity", "confirmedAt" } ] }</c>, keyed by
/// the normalized <see cref="SshRoute"/> — so the same private address behind two bastions never collides
/// (M14.4b-1 H1). The jump host itself is trusted through the ordinary DIRECT store.
/// <para>
/// Mirrors <see cref="JsonHostKeyTrustStore"/>: same conflict semantics, same fail-closed loading (an
/// invalid file blocks every routed lookup and is never overwritten). A newer <c>schemaVersion</c> fails
/// closed the same way and is therefore never rewritten. This store never opens the direct file.
/// </para>
/// </summary>
public sealed class JsonRoutedHostKeyTrustStore(
    RoutedHostKeyTrustStorageOptions storageOptions,
    ILogger<JsonRoutedHostKeyTrustStore> logger) : IRoutedHostKeyTrustStore, IDisposable
{
    internal const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<SshRoute, TrustedRoutedHostKey>? _entries;

    public async Task<TrustedRoutedHostKey?> GetAsync(
        SshRoute route,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(route);

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
        SshRoute route,
        HostKeyIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var normalizedRoute = Normalize(route);
        var normalizedIdentity = HostKeyIdentity.Create(identity.Algorithm, identity.Sha256Fingerprint);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            if (_entries!.TryGetValue(normalizedRoute, out var existing))
            {
                if (!existing.Identity.Matches(normalizedIdentity))
                {
                    throw new HostKeyTrustConflictException();
                }

                return;
            }

            _entries[normalizedRoute] = new TrustedRoutedHostKey
            {
                Route = normalizedRoute,
                Identity = normalizedIdentity,
                ConfirmedAt = DateTimeOffset.UtcNow
            };
            await SaveAsync(cancellationToken);
            logger.LogInformation(
                "Stored routed SSH host trust for {Target} via {Via}.",
                normalizedRoute.Target,
                normalizedRoute.Via);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(
        SshRoute route,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(route);

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

    private static SshRoute Normalize(SshRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return SshRoute.Create(route.Via, route.Target);
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

        var loadedEntries = new Dictionary<SshRoute, TrustedRoutedHostKey>();
        try
        {
            await using var stream = new FileStream(
                storageOptions.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                useAsync: true);
            var persisted = await JsonSerializer.DeserializeAsync<RoutedTrustFile>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                ?? throw new InvalidDataException("The routed SSH host trust file is empty.");

            if (persisted.SchemaVersion is not { } schemaVersion || schemaVersion < 1)
            {
                throw new InvalidDataException("The routed SSH host trust file has no valid schema version.");
            }

            if (schemaVersion > SupportedSchemaVersion)
            {
                // Fail closed and never rewrite: EnsureLoaded keeps throwing, so no save can run.
                throw new InvalidDataException("The routed SSH host trust file uses a newer schema version.");
            }

            foreach (var entry in persisted.Entries
                ?? throw new InvalidDataException("The routed SSH host trust file has no entry list."))
            {
                try
                {
                    if (entry?.Via is null || entry.Endpoint is null || entry.Identity is null)
                    {
                        throw new InvalidDataException("The routed SSH host trust entry is incomplete.");
                    }

                    var route = SshRoute.Create(entry.Via, entry.Endpoint);
                    var identity = HostKeyIdentity.Create(
                        entry.Identity.Algorithm,
                        entry.Identity.Sha256Fingerprint);
                    if (!loadedEntries.TryAdd(
                            route,
                            new TrustedRoutedHostKey
                            {
                                Route = route,
                                Identity = identity,
                                ConfirmedAt = entry.ConfirmedAt
                            }))
                    {
                        throw new InvalidDataException("The routed SSH host trust file contains a duplicate route.");
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException)
                {
                    throw new InvalidDataException("The routed SSH host trust file contains an invalid entry.", exception);
                }
            }

            _entries = loadedEntries;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            logger.LogWarning(
                "The routed SSH host trust file is invalid; routed SSH connections remain blocked. Exception type: {ExceptionType}.",
                exception.GetType().Name);
            throw new InvalidDataException("The routed SSH host trust file is invalid.", exception);
        }
        catch (IOException exception)
        {
            logger.LogWarning(
                "The routed SSH host trust file could not be read; routed SSH connections remain blocked. Exception type: {ExceptionType}.",
                exception.GetType().Name);
            throw;
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(storageOptions.FilePath)
            ?? throw new InvalidOperationException("The routed host trust storage path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryFile = storageOptions.FilePath + ".tmp";

        var file = new RoutedTrustFile
        {
            SchemaVersion = SupportedSchemaVersion,
            Entries = _entries!.Values
                .OrderBy(entry => entry.Route.Via.Host, StringComparer.Ordinal)
                .ThenBy(entry => entry.Route.Via.Port)
                .ThenBy(entry => entry.Route.Target.Host, StringComparer.Ordinal)
                .ThenBy(entry => entry.Route.Target.Port)
                .Select(entry => new RoutedTrustEntry
                {
                    Via = entry.Route.Via,
                    Endpoint = entry.Route.Target,
                    Identity = entry.Identity,
                    ConfirmedAt = entry.ConfirmedAt
                })
                .ToList<RoutedTrustEntry?>()
        };

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
                await JsonSerializer.SerializeAsync(stream, file, SerializerOptions, cancellationToken);
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

    private sealed record RoutedTrustFile
    {
        public int? SchemaVersion { get; init; }

        public List<RoutedTrustEntry?>? Entries { get; init; }
    }

    private sealed record RoutedTrustEntry
    {
        public SshEndpoint? Via { get; init; }

        public SshEndpoint? Endpoint { get; init; }

        public HostKeyIdentity? Identity { get; init; }

        public DateTimeOffset ConfirmedAt { get; init; }
    }
}
