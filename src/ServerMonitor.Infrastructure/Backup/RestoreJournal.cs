using System.Security.Cryptography;
using System.Text.Json;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.Infrastructure.Backup;

/// <summary>
/// The closed participant-ID → path table (M14.6 §5.2, Vigil V4/C-8). The journal stores only these IDs; a
/// path is NEVER read from the journal. Built from the same options objects the stores use.
/// </summary>
internal sealed class RestoreParticipantMap
{
    public const string TrustDirect = "trust.direct";
    public const string TrustRouted = "trust.routed";
    public const string ServersRouted = "servers.routed";
    public const string ServersDirect = "servers.direct";
    public const string SettingsNotification = "settings.notification";
    public const string SettingsBackground = "settings.background";

    /// <summary>Step 3 write order: trust, then routed servers BEFORE direct servers (a route never degrades
    /// even without the journal), then settings.</summary>
    public static IReadOnlyList<string> WriteOrder { get; } =
        [TrustDirect, TrustRouted, ServersRouted, ServersDirect, SettingsNotification, SettingsBackground];

    private readonly Dictionary<string, string> _paths;

    public RestoreParticipantMap(
        ServerStorageOptions servers,
        HostKeyTrustStorageOptions trust,
        RoutedHostKeyTrustStorageOptions routedTrust,
        string notificationSettingsPath,
        string backgroundSettingsPath)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(routedTrust);
        ArgumentException.ThrowIfNullOrEmpty(notificationSettingsPath);
        ArgumentException.ThrowIfNullOrEmpty(backgroundSettingsPath);

        _paths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TrustDirect] = trust.FilePath,
            [TrustRouted] = routedTrust.FilePath,
            [ServersRouted] = servers.RoutedFilePath,
            [ServersDirect] = servers.FilePath,
            [SettingsNotification] = notificationSettingsPath,
            [SettingsBackground] = backgroundSettingsPath,
        };

        JournalDirectory = RestoreJournal.DirectoryFor(servers);
    }

    public string JournalDirectory { get; }

    public string PathOf(string participant) =>
        _paths.TryGetValue(participant, out var path)
            ? path
            : throw new ArgumentOutOfRangeException(nameof(participant));

    public static bool IsKnown(string participant) => WriteOrder.Contains(participant, StringComparer.Ordinal);
}

internal sealed record JournalFileEntry(string Participant, bool Existed, string? Sha256);

internal enum JournalState
{
    Applying,
    Committed,
}

internal sealed record JournalManifest(
    JournalState State,
    IReadOnlyList<JournalFileEntry> Files,
    IReadOnlyList<CredentialReference> NewCredentials,
    IReadOnlyList<CredentialReference> OldCredentials);

internal enum JournalReadStatus
{
    /// <summary>No journal directory.</summary>
    None,

    /// <summary>The directory exists without a manifest: a crash during step 1, before any mutation.</summary>
    Orphan,

    Valid,

    /// <summary>Unreadable, hash-mismatched or otherwise inconsistent: nothing destructive may be done.</summary>
    Corrupt,
}

internal sealed record JournalReadResult(
    JournalReadStatus Status,
    JournalManifest? Manifest,
    IReadOnlyDictionary<string, byte[]>? PreContent);

/// <summary>
/// The on-disk restore journal (§5.2): <c>restore-journal\pre\&lt;participant&gt;</c> byte copies plus
/// <c>journal.json</c>. It holds no secret (credential references are identifiers; config copies have the
/// sensitivity of the originals). Written durably (WriteThrough + Flush(true) + atomic move), manifest LAST.
/// Read back with the same strict overlay as the backup payload: any inconsistency is <see cref="JournalReadStatus.Corrupt"/>.
/// </summary>
internal sealed class RestoreJournal(string directory)
{
    public const string DirectoryName = "restore-journal";
    public const string ManifestFileName = "journal.json";
    public const string PreDirectoryName = "pre";
    public const int CurrentVersion = 1;

    private const string VersionProperty = "journalVersion";
    private const string StateProperty = "state";
    private const string FilesProperty = "files";
    private const string NewCredentialsProperty = "newCredentials";
    private const string OldCredentialsProperty = "oldCredentials";
    private const string ParticipantProperty = "participant";
    private const string ExistedProperty = "existed";
    private const string Sha256Property = "sha256";
    private const string ServerIdProperty = "serverId";
    private const string KindProperty = "kind";
    private const string ReferenceIdProperty = "referenceId";

    public string Directory { get; } = directory;

    public string ManifestPath => Path.Combine(Directory, ManifestFileName);

    public string PreDirectory => Path.Combine(Directory, PreDirectoryName);

    public bool Exists => System.IO.Directory.Exists(Directory);

    public static string DirectoryFor(ServerStorageOptions servers) =>
        Path.Combine(Path.GetDirectoryName(servers.FilePath) ?? string.Empty, DirectoryName);

    /// <summary>Step 1a: copies every participant's current bytes to <c>pre\</c> and returns their entries.</summary>
    public IReadOnlyList<JournalFileEntry> CopyPre(RestoreParticipantMap map)
    {
        System.IO.Directory.CreateDirectory(PreDirectory);
        var entries = new List<JournalFileEntry>();
        foreach (var participant in RestoreParticipantMap.WriteOrder)
        {
            var current = DurableFile.ReadOrNull(map.PathOf(participant));
            if (current is null)
            {
                entries.Add(new JournalFileEntry(participant, false, null));
                continue;
            }

            DurableFile.Replace(Path.Combine(PreDirectory, participant), current);
            entries.Add(new JournalFileEntry(participant, true, Sha256Hex(current)));
        }

        return entries;
    }

    public void WriteManifest(JournalManifest manifest)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber(VersionProperty, CurrentVersion);
            writer.WriteString(StateProperty, manifest.State.ToString());
            writer.WritePropertyName(FilesProperty);
            writer.WriteStartArray();
            foreach (var file in manifest.Files)
            {
                writer.WriteStartObject();
                writer.WriteString(ParticipantProperty, file.Participant);
                writer.WriteBoolean(ExistedProperty, file.Existed);
                if (file.Sha256 is null)
                {
                    writer.WriteNull(Sha256Property);
                }
                else
                {
                    writer.WriteString(Sha256Property, file.Sha256);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            WriteReferences(writer, NewCredentialsProperty, manifest.NewCredentials);
            WriteReferences(writer, OldCredentialsProperty, manifest.OldCredentials);
            writer.WriteEndObject();
        }

        DurableFile.Replace(ManifestPath, buffer.ToArray());
    }

    public JournalReadResult Read()
    {
        if (!Exists)
        {
            return new JournalReadResult(JournalReadStatus.None, null, null);
        }

        if (!File.Exists(ManifestPath))
        {
            return new JournalReadResult(JournalReadStatus.Orphan, null, null);
        }

        try
        {
            var manifest = ParseManifest(File.ReadAllBytes(ManifestPath));
            if (manifest is null)
            {
                return Corrupt();
            }

            var pre = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var file in manifest.Files.Where(file => file.Existed))
            {
                var prePath = Path.Combine(PreDirectory, file.Participant);
                if (!File.Exists(prePath))
                {
                    return Corrupt();
                }

                var bytes = File.ReadAllBytes(prePath);
                if (!string.Equals(Sha256Hex(bytes), file.Sha256, StringComparison.Ordinal))
                {
                    return Corrupt();
                }

                pre[file.Participant] = bytes;
            }

            return new JournalReadResult(JournalReadStatus.Valid, manifest, pre);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Corrupt();
        }
    }

    public void Delete()
    {
        if (Exists)
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static JournalReadResult Corrupt() => new(JournalReadStatus.Corrupt, null, null);

    private static void WriteReferences(Utf8JsonWriter writer, string name, IReadOnlyList<CredentialReference> references)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var reference in references)
        {
            writer.WriteStartObject();
            writer.WriteString(ServerIdProperty, reference.ServerId);
            writer.WriteString(KindProperty, BackupCredentialKindNames.ToName(reference.Kind));
            writer.WriteString(ReferenceIdProperty, reference.ReferenceId);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // Strict: exact names, no unknown or duplicate members, exactly the six known participants once each,
    // well-formed hashes and references, no reference in both lists.
    private static JournalManifest? ParseManifest(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, BackupPayloadSerializer.StrictDocumentOptions);
            var root = document.RootElement;
            if (!HasExactProperties(root, [VersionProperty, StateProperty, FilesProperty, NewCredentialsProperty, OldCredentialsProperty])
                || !root.GetProperty(VersionProperty).TryGetInt32(out var version)
                || version != CurrentVersion
                || root.GetProperty(StateProperty).ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var state = root.GetProperty(StateProperty).GetString() switch
            {
                nameof(JournalState.Applying) => JournalState.Applying,
                nameof(JournalState.Committed) => JournalState.Committed,
                _ => (JournalState?)null
            };
            if (state is null)
            {
                return null;
            }

            var filesElement = root.GetProperty(FilesProperty);
            if (filesElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var files = new List<JournalFileEntry>();
            foreach (var element in filesElement.EnumerateArray())
            {
                if (!HasExactProperties(element, [ParticipantProperty, ExistedProperty, Sha256Property])
                    || element.GetProperty(ParticipantProperty).ValueKind != JsonValueKind.String
                    || element.GetProperty(ExistedProperty).ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return null;
                }

                var participant = element.GetProperty(ParticipantProperty).GetString()!;
                var existed = element.GetProperty(ExistedProperty).GetBoolean();
                var hash = element.GetProperty(Sha256Property);
                string? sha = null;
                if (existed)
                {
                    if (hash.ValueKind != JsonValueKind.String
                        || hash.GetString() is not { Length: 64 } value
                        || !value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'))
                    {
                        return null;
                    }

                    sha = value;
                }
                else if (hash.ValueKind != JsonValueKind.Null)
                {
                    return null;
                }

                if (!RestoreParticipantMap.IsKnown(participant) || files.Exists(file => file.Participant == participant))
                {
                    return null;
                }

                files.Add(new JournalFileEntry(participant, existed, sha));
            }

            if (files.Count != RestoreParticipantMap.WriteOrder.Count)
            {
                return null;
            }

            var newCredentials = ReadReferences(root.GetProperty(NewCredentialsProperty));
            var oldCredentials = ReadReferences(root.GetProperty(OldCredentialsProperty));
            if (newCredentials is null
                || oldCredentials is null
                || newCredentials.Select(reference => reference.ReferenceId)
                    .Intersect(oldCredentials.Select(reference => reference.ReferenceId))
                    .Any())
            {
                return null;
            }

            return new JournalManifest(state.Value, files, newCredentials, oldCredentials);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    private static List<CredentialReference>? ReadReferences(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var references = new List<CredentialReference>();
        foreach (var element in array.EnumerateArray())
        {
            if (!HasExactProperties(element, [ServerIdProperty, KindProperty, ReferenceIdProperty])
                || element.GetProperty(ServerIdProperty).ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(element.GetProperty(ServerIdProperty).GetString(), "D", out var serverId)
                || element.GetProperty(KindProperty).ValueKind != JsonValueKind.String
                || !BackupCredentialKindNames.TryParse(element.GetProperty(KindProperty).GetString(), out var kind)
                || element.GetProperty(ReferenceIdProperty).ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(element.GetProperty(ReferenceIdProperty).GetString(), "D", out var referenceId))
            {
                return null;
            }

            var reference = new CredentialReference(serverId, kind, referenceId);
            if (!reference.IsValid || references.Exists(existing => existing.ReferenceId == referenceId))
            {
                return null;
            }

            references.Add(reference);
        }

        return references;
    }

    private static bool HasExactProperties(JsonElement element, IReadOnlyCollection<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Any(name => property.NameEquals(name)))
            {
                return false;
            }

            count++;
        }

        return count == names.Count;
    }
}
