using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Backup;

namespace ServerMonitor.Infrastructure.Persistence;

/// <summary>
/// Persists servers across two files (M14.4b-1 F3): the legacy <c>servers.json</c> bare array holding only
/// DIRECT servers, and <c>routed-servers.json</c> (<c>{ "schemaVersion": 1, "servers": [...] }</c>) holding
/// only servers with a route. A released build reads only the first, so it can never dial a routed server
/// direct. Invariants (all tested):
/// <list type="number">
/// <item>Per-entry tolerant parse: one bad entry never prevents loading the others.</item>
/// <item>Unknown data survives: unknown entry properties (any casing, nested too), unknown top-level
/// properties of the routed file, and unparsable entries are written back on the next save.</item>
/// <item>A file that is not valid JSON / not the expected shape is copied byte-for-byte to
/// <c>&lt;name&gt;.corrupt-&lt;yyyyMMddTHHmmssfffZ&gt;</c> before anything replaces it.</item>
/// <item>A routed file with a newer <c>schemaVersion</c> is read-only: not loaded, never rewritten.</item>
/// <item>A route never degrades to direct: a <c>servers.json</c> entry carrying <c>route</c>/<c>jump</c>,
/// or a routed entry without a usable route, is quarantined (preserved, not loaded).</item>
/// <item>Moves are crash-safe toward the route; an id in both files loads the routed copy and the next
/// save removes the duplicate.</item>
/// <item>Each file is written atomically (<c>.tmp</c> + <see cref="File.Move(string, string, bool)"/>).</item>
/// </list>
/// Every save re-reads both files under the gate, so nothing on disk is lost to a stale view; an entry
/// whose id this instance never handed out is treated as unknown data and preserved, never deleted.
/// </summary>
public sealed class JsonServerRepository(
    ServerStorageOptions storageOptions,
    ILogger<JsonServerRepository> logger,
    IConfigurationWriteGate writeGate) : IServerRepository, IServerLoadDiagnosisSource, IDisposable
{
    internal const int SupportedRoutedSchemaVersion = 1;

    private const string SchemaVersionProperty = "schemaVersion";
    private const string ServersProperty = "servers";
    private const string RouteProperty = "route";
    private const string JumpProperty = "jump";
    private const string IdProperty = "id";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    // Ids this instance has returned or been given. A save deletes an on-disk entry only when its id is in
    // here and absent from the new list; any other entry is someone else's data and is kept.
    private readonly HashSet<Guid> _knownIds = [];

    public async Task<ServerLoadStatus> GetLoadStatusAsync(CancellationToken cancellationToken = default) =>
        (await GetLoadDiagnosisAsync(cancellationToken)).Status;

    public async Task<ServerLoadDiagnosis> GetLoadDiagnosisAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // File.Exists suppresses access errors. Probe attributes first so denied access cannot
            // masquerade as a first run. Missing parent directories are legitimate absence.
            foreach (var path in new[] { storageOptions.FilePath, storageOptions.RoutedFilePath })
            {
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                        return new(ServerLoadStatus.Unavailable, 0);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }

            var direct = await ReadDirectAsync(cancellationToken);
            var routed = await ReadRoutedAsync(cancellationToken);
            if (direct.IsCorrupt || routed.IsCorrupt || routed.IsReadOnly)
                return new(ServerLoadStatus.Unavailable, 0);
            if (!direct.Exists && !routed.Exists)
                return new(ServerLoadStatus.NotFound, 0);

            var validCount = routed.Entries.Count + DirectEntriesToLoad(direct, routed).Count();
            var quarantinedCount = direct.Quarantined.Count + routed.Quarantined.Count;
            return new(validCount == 0 && quarantinedCount > 0
                ? ServerLoadStatus.Unavailable : ServerLoadStatus.Loaded, validCount);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(ServerLoadStatus.Unavailable, 0);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<Server>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var direct = await ReadDirectAsync(cancellationToken);
            var routed = await ReadRoutedAsync(cancellationToken);
            var loaded = routed.Entries.Select(entry => entry.Server)
                .Concat(DirectEntriesToLoad(direct, routed).Select(entry => entry.Server))
                .ToList();
            _knownIds.UnionWith(loaded.Select(server => server.Id));
            return loaded;
        }
        catch (IOException exception)
        {
            logger.LogWarning(
                "The local server configuration could not be read. Exception type: {ExceptionType}.",
                exception.GetType().Name);
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAllAsync(
        IReadOnlyCollection<Server> servers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Defence in depth behind ServerService (M14.6 V5): checked inside our own lock.
            using var lease = writeGate.EnterWrite();
            var directory = Path.GetDirectoryName(storageOptions.FilePath)
                ?? throw new InvalidOperationException("The server storage path has no directory.");
            Directory.CreateDirectory(directory);

            // Re-read under the gate: an IOException here propagates, so nothing that could not be read
            // is ever overwritten.
            var direct = await ReadDirectAsync(cancellationToken);
            var routed = await ReadRoutedAsync(cancellationToken);

            var newDirect = servers.Where(server => server.Route is null).ToList();
            var newRouted = servers.Where(server => server.Route is not null).ToList();
            if (routed.IsReadOnly && newRouted.Count > 0)
            {
                throw new InvalidOperationException(
                    "The routed server file was written by a newer version and is read-only; routed servers cannot be saved.");
            }

            var savedIds = servers.Select(server => server.Id).ToHashSet();
            var originals = new Dictionary<Guid, JsonElement>();
            foreach (var entry in DirectEntriesToLoad(direct, routed).Concat(routed.Entries))
            {
                originals[entry.Server.Id] = entry.Raw; // routed last: the routed copy wins
            }

            var onDiskDirectIds = DirectEntriesToLoad(direct, routed).Select(entry => entry.Server.Id).ToHashSet();
            var onDiskRoutedIds = routed.Entries.Select(entry => entry.Server.Id).ToHashSet();
            var gains = newRouted.Where(server => onDiskDirectIds.Contains(server.Id)).ToList();
            var losses = newDirect.Where(server => onDiskRoutedIds.Contains(server.Id)).ToList();

            // A direct duplicate of a LOADED routed server is dropped (the routed copy won); a direct copy of an
            // id the routed file claims but could not load is kept verbatim, never deleted and never loaded.
            var shadowedDirect = direct.Entries
                .Where(entry => routed.ClaimedIds.Contains(entry.Server.Id) && !onDiskRoutedIds.Contains(entry.Server.Id))
                .Select(entry => entry.Raw);
            var directOutput = BuildArray(
                newDirect,
                originals,
                forDirectFile: true,
                preserved: UnknownEntries(DirectEntriesToLoad(direct, routed), savedIds)
                    .Concat(shadowedDirect)
                    .Concat(direct.Quarantined));

            var routedPreserved = UnknownEntries(routed.Entries, savedIds).Concat(routed.Quarantined).ToList();
            var routedOutput = BuildRoutedFile(newRouted, originals, routedPreserved, routed.TopLevel);
            var writeRouted = !routed.IsReadOnly
                && (routed.Exists || newRouted.Count > 0 || routedPreserved.Count > 0);

            // All-or-nothing from the caller's view: every file is journaled with its pre-save bytes, and if a
            // later write fails the earlier ones are restored before the failure is rethrown. The ordering below
            // is what protects a real crash (no rollback runs); the journal protects a thrown failure.
            var journal = new List<JournalEntry>();
            try
            {
                if (losses.Count == 0)
                {
                    // Gaining (or keeping) routes: the routed copy lands first. A crash between the two writes
                    // leaves the id in both files, and the routed copy wins on load.
                    if (writeRouted)
                    {
                        await CommitAsync(journal, storageOptions.RoutedFilePath, routedOutput, routed, cancellationToken);
                    }

                    await CommitAsync(journal, storageOptions.FilePath, directOutput, direct, cancellationToken);
                }
                else
                {
                    // Losing routes: servers.json lands first while the routed copy still exists (it wins on
                    // load). If other servers gain a route in the same save, an intermediate routed file keeps
                    // BOTH the gainers and the losers routed, so no crash point shows any of them as direct only.
                    if (gains.Count > 0)
                    {
                        var intermediate = BuildRoutedFile(
                            newRouted,
                            originals,
                            routedPreserved.Concat(losses.Select(server => originals[server.Id])).ToList(),
                            routed.TopLevel);
                        await CommitAsync(journal, storageOptions.RoutedFilePath, intermediate, routed, cancellationToken);
                        routed = routed with { IsCorrupt = false };
                    }

                    await CommitAsync(journal, storageOptions.FilePath, directOutput, direct, cancellationToken);
                    await CommitAsync(journal, storageOptions.RoutedFilePath, routedOutput, routed, cancellationToken);
                }
            }
            catch (Exception writeFailure) when (journal.Count > 0)
            {
                RollBack(journal, writeFailure);
                throw;
            }

            _knownIds.UnionWith(savedIds);
            logger.LogInformation(
                "Saved {ServerCount} server configurations ({RoutedCount} routed).",
                servers.Count,
                newRouted.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>The model's Web-default serializer options (the backup payload uses the same shape).</summary>
    internal static JsonSerializerOptions ModelSerializerOptions => SerializerOptions;

    /// <summary>
    /// The two files that hold exactly <paramref name="servers"/> and nothing else (restore REPLACE: no
    /// originals, no preserved entries): direct servers in the <c>servers.json</c> array, routed servers in the
    /// <c>routed-servers.json</c> envelope. Built with the same writers as a normal save.
    /// </summary>
    internal static (byte[] Direct, byte[] Routed) RenderReplacement(IReadOnlyCollection<Server> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var none = new Dictionary<Guid, JsonElement>();
        var direct = BuildArray(servers.Where(server => server.Route is null), none, forDirectFile: true, preserved: []);
        var routed = BuildRoutedFile(servers.Where(server => server.Route is not null), none, preserved: [], topLevel: []);
        return (direct, routed);
    }

    /// <summary>
    /// Restore-only replace of one of the two files (<paramref name="content"/> null deletes it), under this
    /// store's lock. Only the restore holding the configuration gate can call it (M14.6 §5.5).
    /// </summary>
    internal async Task ReplaceFileForRestoreAsync(
        RestoreWriteToken token,
        bool routed,
        byte[]? content,
        CancellationToken cancellationToken)
    {
        writeGate.EnsureHeldBy(token);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            DurableFile.Replace(routed ? storageOptions.RoutedFilePath : storageOptions.FilePath, content);
            _knownIds.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads both files with this store's own parser for restore verification. <see langword="null"/> when a
    /// file is corrupt, read-only, or holds any entry that would not load (quarantine).
    /// </summary>
    internal async Task<IReadOnlyList<Server>?> ReadForVerifyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var direct = await ReadDirectAsync(cancellationToken);
            var routed = await ReadRoutedAsync(cancellationToken);
            if (direct.IsCorrupt || routed.IsCorrupt || routed.IsReadOnly
                || direct.Quarantined.Count > 0 || routed.Quarantined.Count > 0
                || direct.Entries.Any(entry => routed.ClaimedIds.Contains(entry.Server.Id)))
            {
                return null;
            }

            return routed.Entries.Select(entry => entry.Server).Concat(direct.Entries.Select(entry => entry.Server)).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stored entries that do not load (quarantined or shadowed), plus one per unreadable file:
    /// the export summary's "could not be read and were not included".</summary>
    internal async Task<int> CountUnloadableAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var direct = await ReadDirectAsync(cancellationToken);
            var routed = await ReadRoutedAsync(cancellationToken);
            return direct.Quarantined.Count
                + routed.Quarantined.Count
                + (direct.IsCorrupt ? 1 : 0)
                + (routed.IsCorrupt ? 1 : 0)
                + direct.Entries.Count(entry => routed.ClaimedIds.Contains(entry.Server.Id) && !routed.Entries.Exists(r => r.Server.Id == entry.Server.Id));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Test-only fault seam: invoked with <c>write:&lt;path&gt;</c> before each file write and
    /// <c>restore:&lt;path&gt;</c> before each rollback step, so a test can make the real file I/O fail at an
    /// exact point. Production never sets it.
    /// </summary>
    internal Action<string>? FaultInjector { get; init; }

    private async Task CommitAsync(
        List<JournalEntry> journal,
        string path,
        byte[] content,
        FileSnapshot current,
        CancellationToken cancellationToken)
    {
        // Journal BEFORE writing: a failure after File.Move (e.g. the temporary's cleanup) must still roll back.
        if (!journal.Exists(entry => entry.Path == path))
        {
            journal.Add(new JournalEntry(path, current.Exists ? current.Bytes : null));
        }

        FaultInjector?.Invoke("write:" + path);
        await WriteAsync(path, content, current, cancellationToken);
    }

    /// <summary>
    /// Restores every journaled file to its pre-save bytes (or deletes it if it did not exist), newest first.
    /// Returns normally when everything is back as it was; otherwise throws
    /// <see cref="ServerPersistencePartialCommitException"/> so the caller keeps every secret the new state
    /// might reference.
    /// </summary>
    private void RollBack(List<JournalEntry> journal, Exception writeFailure)
    {
        List<Exception>? restoreFailures = null;
        for (var index = journal.Count - 1; index >= 0; index--)
        {
            var entry = journal[index];
            try
            {
                if (IsUnchanged(entry))
                {
                    continue;
                }

                FaultInjector?.Invoke("restore:" + entry.Path);

                if (entry.Original is null)
                {
                    File.Delete(entry.Path);
                }
                else
                {
                    RestoreBytes(entry.Path, entry.Original);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                (restoreFailures ??= []).Add(exception);
            }
        }

        if (restoreFailures is null)
        {
            logger.LogWarning(
                "A server configuration save failed ({ExceptionType}); the files already written were restored.",
                writeFailure.GetType().Name);
            return;
        }

        logger.LogError(
            "A server configuration save failed ({ExceptionType}) and {RestoreFailureCount} file(s) could not be restored; the saved state may be partial.",
            writeFailure.GetType().Name,
            restoreFailures.Count);
        throw new ServerPersistencePartialCommitException(
            "The server configuration was only partly saved and could not be restored.",
            new AggregateException([writeFailure, .. restoreFailures]));
    }

    private static bool IsUnchanged(JournalEntry entry)
    {
        var exists = File.Exists(entry.Path);
        return entry.Original is null
            ? !exists
            : exists && File.ReadAllBytes(entry.Path).AsSpan().SequenceEqual(entry.Original);
    }

    private static void RestoreBytes(string path, byte[] original)
    {
        var temporaryFile = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(original);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryFile, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    // A direct entry whose id also appears in the routed file never loads: the routed copy wins, and when
    // the routed copy is itself unusable the direct one is kept as quarantine rather than dialled direct.
    private static IEnumerable<ParsedEntry> DirectEntriesToLoad(FileSnapshot direct, FileSnapshot routed) =>
        direct.Entries.Where(entry => !routed.ClaimedIds.Contains(entry.Server.Id));

    private IEnumerable<JsonElement> UnknownEntries(IEnumerable<ParsedEntry> entries, HashSet<Guid> savedIds) =>
        entries
            .Where(entry => !savedIds.Contains(entry.Server.Id) && !_knownIds.Contains(entry.Server.Id))
            .Select(entry => entry.Raw);

    private async Task<FileSnapshot> ReadDirectAsync(CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(storageOptions.FilePath, cancellationToken);
        if (bytes is null)
        {
            return FileSnapshot.Missing;
        }

        if (!TryParse(bytes, out var document) || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            logger.LogWarning("The local server configuration is invalid and was ignored.");
            return FileSnapshot.Corrupt(bytes);
        }

        using (document)
        {
            var snapshot = FileSnapshot.Parsed(bytes);
            var seen = new HashSet<Guid>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                // Any route/jump in the legacy file is quarantined: a released build would dial it direct,
                // and so must we never.
                var server = HasProperty(element, RouteProperty) || HasProperty(element, JumpProperty)
                    ? null
                    : TryDeserialize(element);
                if (server is null || server.Route is not null || !seen.Add(server.Id))
                {
                    snapshot.Quarantined.Add(element.Clone());
                    continue;
                }

                snapshot.Entries.Add(new ParsedEntry(server, element.Clone()));
            }

            LogQuarantine(snapshot, "servers.json");
            return snapshot;
        }
    }

    private async Task<FileSnapshot> ReadRoutedAsync(CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(storageOptions.RoutedFilePath, cancellationToken);
        if (bytes is null)
        {
            return FileSnapshot.Missing;
        }

        if (!TryParse(bytes, out var document))
        {
            logger.LogWarning("The routed server configuration is not valid JSON and was ignored; no routed id could be recovered.");
            return FileSnapshot.Corrupt(bytes);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                logger.LogWarning("The routed server configuration has an unexpected shape and was ignored.");
                return ClaimRecoverable(FileSnapshot.Corrupt(bytes), root);
            }

            var hasVersion = TryGetProperty(root, SchemaVersionProperty, out var versionElement);
            var hasServers = TryGetProperty(root, ServersProperty, out var serversElement);
            if (!hasVersion
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var schemaVersion)
                || schemaVersion < 1)
            {
                logger.LogWarning("The routed server configuration has no valid schema version and was ignored.");
                return ClaimRecoverable(FileSnapshot.Corrupt(bytes), hasServers ? serversElement : default);
            }

            if (schemaVersion > SupportedRoutedSchemaVersion)
            {
                logger.LogWarning(
                    "The routed server configuration uses schema {SchemaVersion}, newer than supported; it is read-only.",
                    schemaVersion);
                var future = FileSnapshot.ReadOnly(bytes);
                if (hasServers && serversElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in serversElement.EnumerateArray())
                    {
                        if (TryReadId(element) is { } futureId)
                        {
                            future.ClaimedIds.Add(futureId);
                        }
                    }
                }

                return future;
            }

            if (!hasServers || serversElement.ValueKind != JsonValueKind.Array)
            {
                logger.LogWarning("The routed server configuration has no server list and was ignored.");
                return FileSnapshot.Corrupt(bytes);
            }

            var snapshot = FileSnapshot.Parsed(bytes);
            snapshot.TopLevel = root.EnumerateObject()
                .Where(property => !IsNamed(property, SchemaVersionProperty) && !IsNamed(property, ServersProperty))
                .Select(property => new PreservedProperty(property.Name, property.Value.Clone()))
                .ToList();

            var seen = new HashSet<Guid>();
            foreach (var element in serversElement.EnumerateArray())
            {
                if (TryReadId(element) is { } claimedId)
                {
                    snapshot.ClaimedIds.Add(claimedId);
                }

                var server = TryDeserialize(element);
                if (server?.Route?.Jump is null || !seen.Add(server.Id))
                {
                    snapshot.Quarantined.Add(element.Clone());
                    continue;
                }

                snapshot.Entries.Add(new ParsedEntry(server, element.Clone()));
            }

            LogQuarantine(snapshot, ServerStorageOptions.RoutedFileName);
            return snapshot;
        }
    }

    /// <summary>
    /// An invalid routed file may still list server entries. Their ids stay CLAIMED, so a crash-left direct
    /// copy of a routed server never loads as direct, and the entries are kept as quarantine so the claim
    /// survives the save that replaces the invalid file (the original is backed up byte-for-byte first).
    /// </summary>
    private FileSnapshot ClaimRecoverable(FileSnapshot corrupt, JsonElement candidates)
    {
        if (candidates.ValueKind != JsonValueKind.Array)
        {
            logger.LogWarning("No routed server ids could be recovered from the invalid routed configuration.");
            return corrupt;
        }

        foreach (var element in candidates.EnumerateArray())
        {
            if (TryReadId(element) is { } claimedId)
            {
                corrupt.ClaimedIds.Add(claimedId);
            }

            corrupt.Quarantined.Add(element.Clone());
        }

        return corrupt;
    }

    private void LogQuarantine(FileSnapshot snapshot, string fileName)
    {
        if (snapshot.Quarantined.Count > 0)
        {
            logger.LogWarning(
                "{QuarantinedCount} entries in {FileName} could not be loaded; they are preserved unchanged.",
                snapshot.Quarantined.Count,
                fileName);
        }
    }

    private static Server? TryDeserialize(JsonElement element) => TryDeserialize(element, SerializerOptions);

    /// <summary>
    /// One server entry, or <see langword="null"/> when it does not bind or has an empty id. The options are a
    /// parameter so the backup reader can apply its strict overlay while the store keeps its lenient load path.
    /// </summary>
    internal static Server? TryDeserialize(JsonElement element, JsonSerializerOptions options)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            var server = element.Deserialize<Server>(options);
            return server is null || server.Id == Guid.Empty ? null : server;
        }
        catch (Exception exception) when (exception is JsonException
            or NotSupportedException
            or InvalidOperationException
            or ArgumentException
            or FormatException)
        {
            return null;
        }
    }

    private static Guid? TryReadId(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && TryGetProperty(element, IdProperty, out var id)
        && id.ValueKind == JsonValueKind.String
        && Guid.TryParse(id.GetString(), out var parsed)
        && parsed != Guid.Empty
            ? parsed
            : null;

    private static bool HasProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Any(property => IsNamed(property, name));

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (IsNamed(property, name))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsNamed(JsonProperty property, string name) =>
        string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase);

    private static bool TryParse(byte[] bytes, out JsonDocument document)
    {
        ReadOnlyMemory<byte> payload = bytes;
        if (payload.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            payload = payload[3..];
        }

        try
        {
            document = JsonDocument.Parse(payload, DocumentOptions);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static async Task<byte[]?> ReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static byte[] BuildArray(
        IEnumerable<Server> servers,
        IReadOnlyDictionary<Guid, JsonElement> originals,
        bool forDirectFile,
        IEnumerable<JsonElement> preserved)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteServers(writer, servers, originals, forDirectFile, preserved);
        }

        return buffer.ToArray();
    }

    private static byte[] BuildRoutedFile(
        IEnumerable<Server> servers,
        IReadOnlyDictionary<Guid, JsonElement> originals,
        IEnumerable<JsonElement> preserved,
        IReadOnlyList<PreservedProperty> topLevel)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber(SchemaVersionProperty, SupportedRoutedSchemaVersion);
            writer.WritePropertyName(ServersProperty);
            WriteServers(writer, servers, originals, forDirectFile: false, preserved);
            foreach (var property in topLevel)
            {
                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteServers(
        Utf8JsonWriter writer,
        IEnumerable<Server> servers,
        IReadOnlyDictionary<Guid, JsonElement> originals,
        bool forDirectFile,
        IEnumerable<JsonElement> preserved)
    {
        writer.WriteStartArray();
        foreach (var server in servers)
        {
            var fresh = JsonSerializer.SerializeToElement(server, SerializerOptions);
            JsonElement? original = originals.TryGetValue(server.Id, out var raw) ? raw : null;
            WriteMerged(writer, fresh, original, SerializerOptions.GetTypeInfo(typeof(Server)), forDirectFile);
        }

        foreach (var element in preserved)
        {
            element.WriteTo(writer);
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Writes <paramref name="fresh"/> and then every property of <paramref name="original"/> the model does
    /// not know (compared case-insensitively against the model's full property list, so a known property
    /// omitted as null is never resurrected). Known nested objects merge recursively.
    /// </summary>
    private static void WriteMerged(
        Utf8JsonWriter writer,
        JsonElement fresh,
        JsonElement? original,
        JsonTypeInfo typeInfo,
        bool forDirectFile)
    {
        var hasOriginal = original is { ValueKind: JsonValueKind.Object };
        writer.WriteStartObject();
        foreach (var property in fresh.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            var model = typeInfo.Properties.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, property.Name, StringComparison.OrdinalIgnoreCase));
            if (hasOriginal
                && model is not null
                && property.Value.ValueKind == JsonValueKind.Object
                && TryGetProperty(original!.Value, property.Name, out var nestedOriginal)
                && nestedOriginal.ValueKind == JsonValueKind.Object)
            {
                WriteMerged(
                    writer,
                    property.Value,
                    nestedOriginal,
                    SerializerOptions.GetTypeInfo(Nullable.GetUnderlyingType(model.PropertyType) ?? model.PropertyType),
                    forDirectFile: false);
                continue;
            }

            property.Value.WriteTo(writer);
        }

        if (hasOriginal)
        {
            foreach (var property in original!.Value.EnumerateObject())
            {
                var known = typeInfo.Properties.Any(candidate =>
                    string.Equals(candidate.Name, property.Name, StringComparison.OrdinalIgnoreCase));

                // servers.json must never gain a route-like property, or the entry would be quarantined.
                var routeLike = forDirectFile && (IsNamed(property, RouteProperty) || IsNamed(property, JumpProperty));
                if (known || routeLike)
                {
                    continue;
                }

                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private async Task WriteAsync(
        string path,
        byte[] content,
        FileSnapshot current,
        CancellationToken cancellationToken)
    {
        if (current.IsCorrupt)
        {
            BackUpCorrupt(path, current.Bytes!);
        }

        var temporaryFile = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryFile,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryFile, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    // Byte-for-byte copy of exactly what was judged unreadable. CreateNew never replaces an earlier backup.
    private void BackUpCorrupt(string path, byte[] bytes)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        for (var attempt = 0; ; attempt++)
        {
            var backup = attempt == 0
                ? $"{path}.corrupt-{stamp}"
                : $"{path}.corrupt-{stamp}-{attempt}";
            try
            {
                using var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                logger.LogWarning("An unreadable server configuration file was backed up before being replaced.");
                return;
            }
            catch (IOException exception) when (File.Exists(backup) && attempt < 100)
            {
                logger.LogDebug("Backup name taken ({ExceptionType}); trying the next one.", exception.GetType().Name);
            }
        }
    }

    private sealed record ParsedEntry(Server Server, JsonElement Raw);

    /// <summary>A file this save touches and its bytes before the save (<see langword="null"/>: it did not exist).</summary>
    private sealed record JournalEntry(string Path, byte[]? Original);

    private sealed record PreservedProperty(string Name, JsonElement Value);

    private sealed record FileSnapshot
    {
        public static FileSnapshot Missing => new();

        public bool Exists { get; init; }

        public byte[]? Bytes { get; init; }

        public bool IsCorrupt { get; init; }

        public bool IsReadOnly { get; init; }

        public List<ParsedEntry> Entries { get; } = [];

        public List<JsonElement> Quarantined { get; } = [];

        /// <summary>Every id the routed file claims, loaded or not: a direct copy of these never loads.</summary>
        public HashSet<Guid> ClaimedIds { get; } = [];

        public IReadOnlyList<PreservedProperty> TopLevel { get; set; } = [];

        public static FileSnapshot Parsed(byte[] bytes) => new() { Exists = true, Bytes = bytes };

        public static FileSnapshot Corrupt(byte[] bytes) => new() { Exists = true, Bytes = bytes, IsCorrupt = true };

        public static FileSnapshot ReadOnly(byte[] bytes) => new() { Exists = true, Bytes = bytes, IsReadOnly = true };
    }
}
