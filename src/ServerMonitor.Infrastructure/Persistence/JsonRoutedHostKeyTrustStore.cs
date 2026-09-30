using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Backup;

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
    ILogger<JsonRoutedHostKeyTrustStore> logger,
    IConfigurationWriteGate writeGate) : IRoutedHostKeyTrustStore, IDisposable
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
            using var lease = writeGate.EnterWrite();
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

    /// <summary>Export (M14.6 H-4, N2): normalized entries whose route is in <paramref name="referenced"/>, and
    /// how many were left out. An invalid or unreadable file throws, so a backup never silently drops trust.</summary>
    internal async Task<(IReadOnlyList<TrustedRoutedHostKey> Entries, int Excluded)> ExportReferencedAsync(
        IReadOnlySet<SshRoute> referenced,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(referenced);
        var all = await ExportAllAsync(cancellationToken);
        var included = all.Where(entry => referenced.Contains(entry.Route)).ToList();
        return (included, all.Count - included.Count);
    }

    /// <summary>Every entry, normalized, in file order (the store's own parser).</summary>
    internal async Task<IReadOnlyList<TrustedRoutedHostKey>> ExportAllAsync(CancellationToken cancellationToken)
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

    /// <summary>The file bytes (schema envelope included) for <paramref name="entries"/>, exactly as a normal
    /// save writes them.</summary>
    internal static byte[] Render(IEnumerable<TrustedRoutedHostKey> entries) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new RoutedTrustFile
            {
                SchemaVersion = SupportedSchemaVersion,
                Entries = Order(entries).Select(ToEntry).ToList<RoutedTrustEntry?>()
            },
            SerializerOptions);

    /// <summary>Entries in the file's entry shape (the backup payload's <c>routedKnownHosts</c>).</summary>
    internal static RoutedTrustEntry ToEntry(TrustedRoutedHostKey entry) => new()
    {
        Via = entry.Route.Via,
        Endpoint = entry.Route.Target,
        Identity = entry.Identity,
        ConfirmedAt = entry.ConfirmedAt
    };

    /// <summary>Parses a JSON array of entries with the given options (the backup reader passes its strict
    /// overlay) and applies the store's normalization and duplicate rule. Throws <see cref="InvalidDataException"/>.</summary>
    internal static IReadOnlyList<TrustedRoutedHostKey> ParseEntries(JsonElement array, JsonSerializerOptions options)
    {
        List<RoutedTrustEntry?>? persisted;
        try
        {
            persisted = array.Deserialize<List<RoutedTrustEntry?>>(options);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidDataException("The routed SSH host trust entries are invalid.", exception);
        }

        return Order(NormalizeEntries(persisted ?? throw new InvalidDataException("The routed SSH host trust entries are missing.")).Values).ToList();
    }

    private static IOrderedEnumerable<TrustedRoutedHostKey> Order(IEnumerable<TrustedRoutedHostKey> entries) =>
        entries
            .OrderBy(entry => entry.Route.Via.Host, StringComparer.Ordinal)
            .ThenBy(entry => entry.Route.Via.Port)
            .ThenBy(entry => entry.Route.Target.Host, StringComparer.Ordinal)
            .ThenBy(entry => entry.Route.Target.Port);

    private static Dictionary<SshRoute, TrustedRoutedHostKey> NormalizeEntries(IEnumerable<RoutedTrustEntry?> persisted)
    {
        var loadedEntries = new Dictionary<SshRoute, TrustedRoutedHostKey>();
        foreach (var entry in persisted)
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

        return loadedEntries;
    }

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

            var loadedEntries = NormalizeEntries(persisted.Entries
                ?? throw new InvalidDataException("The routed SSH host trust file has no entry list."));

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

    private sealed record RoutedTrustFile
    {
        public int? SchemaVersion { get; init; }

        public List<RoutedTrustEntry?>? Entries { get; init; }
    }

    internal sealed record RoutedTrustEntry
    {
        public SshEndpoint? Via { get; init; }

        public SshEndpoint? Endpoint { get; init; }

        public HostKeyIdentity? Identity { get; init; }

        public DateTimeOffset ConfirmedAt { get; init; }
    }
}
