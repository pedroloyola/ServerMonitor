using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.Infrastructure.Backup;

/// <summary>The decrypted backup content (spec §2.1). Owns the secrets; disposing zeroes them.</summary>
internal sealed class BackupPayload : IDisposable
{
    public const int CurrentSchemaVersion = 1;

    public required DateTimeOffset CreatedAt { get; init; }

    public required string AppVersion { get; init; }

    public required IReadOnlyList<Server> Servers { get; init; }

    public required IReadOnlyList<BackupCredential> Credentials { get; init; }

    public required IReadOnlyList<(Guid ServerId, ServerCredentialKind Kind)> MissingCredentials { get; init; }

    public required IReadOnlyList<TrustedHostKey> KnownHosts { get; init; }

    public required IReadOnlyList<TrustedRoutedHostKey> RoutedKnownHosts { get; init; }

    public required PortableSettings Settings { get; init; }

    public void Dispose()
    {
        foreach (var credential in Credentials)
        {
            credential.Secret.Dispose();
        }
    }

    public override string ToString() => nameof(BackupPayload);
}

internal readonly record struct BackupPayloadReadResult(BackupPayload? Payload, BackupError? Error);

/// <summary>
/// Writes and strictly reads the backup payload (spec §2.1, Vigil V2/V7/V9/C-5).
/// <list type="bullet">
/// <item>Secrets travel as <c>secretUtf8Hex</c>: hex digits never need JSON escaping, so the writer rents no
/// escaping scratch, and the reader rejects any escaped value, so no unescape path runs. Hex and UTF-8 scratch
/// is pinned and zeroed; secrets become <see cref="SecretValue"/>, never a <see cref="string"/>.</item>
/// <item>The reader parses the pinned plaintext in place (<see cref="JsonDocument"/> over
/// <see cref="ReadOnlyMemory{T}"/> does not copy it) with ONE strict overlay: exact-case names, unknown members
/// and duplicate properties rejected, strict numbers, no comments or trailing commas — at every level,
/// including the elements handed to the stores' shared parsers.</item>
/// <item>Every rule of §2.1/§3 is checked here, in memory, before anything is written.</item>
/// </list>
/// Failures are codes only: nothing here builds a message from content (C-6).
/// </summary>
internal static class BackupPayloadSerializer
{
    public const int MaxServers = 1000;
    public const int MaxTrustEntries = 4000;
    public const int MaxTextLength = 256;
    public const int MaxPathLength = 32_767;
    public const int MaxAppVersionLength = 64;
    public const int MaxSecretUtf8Bytes = 2560;

    private const string SchemaVersionProperty = "schemaVersion";
    private const string CreatedAtProperty = "createdAt";
    private const string AppVersionProperty = "appVersion";
    private const string ServersProperty = "servers";
    private const string CredentialsProperty = "credentials";
    private const string MissingCredentialsProperty = "missingCredentials";
    private const string KnownHostsProperty = "knownHosts";
    private const string RoutedKnownHostsProperty = "routedKnownHosts";
    private const string SettingsProperty = "settings";
    private const string ServerIdProperty = "serverId";
    private const string KindProperty = "kind";
    private const string ReferenceIdProperty = "referenceId";
    private const string SecretProperty = "secretUtf8Hex";
    private const string NotificationsProperty = "notificationsEnabled";
    private const string BackgroundProperty = "backgroundMonitoringEnabled";

    private static readonly string[] RootProperties =
    [
        SchemaVersionProperty, CreatedAtProperty, AppVersionProperty, ServersProperty, CredentialsProperty,
        MissingCredentialsProperty, KnownHostsProperty, RoutedKnownHostsProperty, SettingsProperty,
    ];

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>The ONE strict overlay (spec §2.1). Public to the assembly so tests and the journal share it.</summary>
    internal static JsonSerializerOptions StrictOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        NumberHandling = JsonNumberHandling.Strict,
    };

    internal static JsonDocumentOptions StrictDocumentOptions { get; } = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 32,
    };

    // ---------------------------------------------------------------- writer

    public static void Write(IBufferWriter<byte> output, BackupPayload payload)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(payload);

        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false });
        var model = JsonServerRepository.ModelSerializerOptions;
        writer.WriteStartObject();
        writer.WriteNumber(SchemaVersionProperty, BackupPayload.CurrentSchemaVersion);
        writer.WriteString(CreatedAtProperty, payload.CreatedAt);
        writer.WriteString(AppVersionProperty, payload.AppVersion);

        writer.WritePropertyName(ServersProperty);
        writer.WriteStartArray();
        foreach (var server in payload.Servers)
        {
            JsonSerializer.Serialize(writer, server, model);
        }

        writer.WriteEndArray();

        writer.WritePropertyName(CredentialsProperty);
        writer.WriteStartArray();
        foreach (var credential in payload.Credentials)
        {
            writer.WriteStartObject();
            writer.WriteString(ServerIdProperty, credential.ServerId);
            writer.WriteString(KindProperty, BackupCredentialKindNames.ToName(credential.Kind));
            writer.WriteString(ReferenceIdProperty, credential.ReferenceId);
            WriteSecretHex(writer, credential.Secret);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName(MissingCredentialsProperty);
        writer.WriteStartArray();
        foreach (var (serverId, kind) in payload.MissingCredentials)
        {
            writer.WriteStartObject();
            writer.WriteString(ServerIdProperty, serverId);
            writer.WriteString(KindProperty, BackupCredentialKindNames.ToName(kind));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName(KnownHostsProperty);
        JsonSerializer.Serialize(writer, payload.KnownHosts.ToList(), model);
        writer.WritePropertyName(RoutedKnownHostsProperty);
        JsonSerializer.Serialize(
            writer,
            payload.RoutedKnownHosts.Select(JsonRoutedHostKeyTrustStore.ToEntry).ToList(),
            model);

        writer.WritePropertyName(SettingsProperty);
        writer.WriteStartObject();
        writer.WriteBoolean(NotificationsProperty, payload.Settings.NotificationsEnabled);
        writer.WriteBoolean(BackgroundProperty, payload.Settings.BackgroundMonitoringEnabled);
        writer.WriteEndObject();

        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteSecretHex(Utf8JsonWriter writer, SecretValue secret)
    {
        var characters = secret.Reveal();
        var utf8 = GC.AllocateArray<byte>(StrictUtf8.GetByteCount(characters), pinned: true);
        var hex = GC.AllocateArray<byte>(utf8.Length * 2, pinned: true);
        try
        {
            StrictUtf8.GetBytes(characters, utf8);
            for (var i = 0; i < utf8.Length; i++)
            {
                hex[2 * i] = HexDigit(utf8[i] >> 4);
                hex[(2 * i) + 1] = HexDigit(utf8[i] & 0xF);
            }

            // Hex digits need no escaping: the writer copies them straight into the zeroing output buffer.
            writer.WriteString(SecretProperty, hex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
            CryptographicOperations.ZeroMemory(hex);
        }
    }

    private static byte HexDigit(int value) => (byte)(value < 10 ? '0' + value : 'a' + value - 10);

    // ---------------------------------------------------------------- reader

    /// <summary>
    /// Strictly parses <paramref name="plaintext"/> (spec §2.1 order: strict UTF-8 → strict JSON → schemaVersion
    /// → structure). Domain rules run in <see cref="Validate"/>.
    /// </summary>
    public static BackupPayloadReadResult Read(ReadOnlyMemory<byte> plaintext)
    {
        var span = plaintext.Span;
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) || !System.Text.Unicode.Utf8.IsValid(span))
        {
            return Invalid();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(plaintext, StrictDocumentOptions);
        }
        catch (JsonException)
        {
            return Invalid();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid();
            }

            // schemaVersion first: a newer payload is IncompatibleVersion even if its shape is unknown to us.
            if (!TryGetExact(root, SchemaVersionProperty, out var schemaElement)
                || schemaElement.ValueKind != JsonValueKind.Number
                || !schemaElement.TryGetInt32(out var schemaVersion))
            {
                return Invalid();
            }

            if (schemaVersion != BackupPayload.CurrentSchemaVersion)
            {
                return new BackupPayloadReadResult(null, BackupError.IncompatibleVersion);
            }

            if (!HasExactProperties(root, RootProperties) || !StringsAreClean(root))
            {
                return Invalid();
            }

            var credentials = new List<BackupCredential>();
            try
            {
                var payload = ReadRoot(root, credentials);
                return payload is null ? Invalid() : new BackupPayloadReadResult(payload, null);
            }
            catch (Exception exception) when (exception is JsonException
                or InvalidDataException
                or InvalidOperationException
                or FormatException
                or ArgumentException
                or NotSupportedException)
            {
                // Partial secrets are zeroed; on success the payload owns them.
                DisposeAll(credentials);
                return Invalid();
            }
        }
    }

    private static BackupPayload? ReadRoot(JsonElement root, List<BackupCredential> credentials)
    {
        var createdAtElement = root.GetProperty(CreatedAtProperty);
        var appVersionElement = root.GetProperty(AppVersionProperty);
        if (createdAtElement.ValueKind != JsonValueKind.String
            || !createdAtElement.TryGetDateTimeOffset(out var createdAt)
            || appVersionElement.ValueKind != JsonValueKind.String
            || appVersionElement.GetString() is not { } appVersion
            || appVersion.Length > MaxAppVersionLength)
        {
            return Fail(credentials);
        }

        var serversElement = root.GetProperty(ServersProperty);
        if (serversElement.ValueKind != JsonValueKind.Array || serversElement.GetArrayLength() > MaxServers)
        {
            return Fail(credentials);
        }

        var servers = new List<Server>();
        foreach (var element in serversElement.EnumerateArray())
        {
            if (JsonServerRepository.TryDeserialize(element, StrictOptions) is not { } server)
            {
                return Fail(credentials);
            }

            servers.Add(server);
        }

        var credentialsElement = root.GetProperty(CredentialsProperty);
        if (credentialsElement.ValueKind != JsonValueKind.Array
            || credentialsElement.GetArrayLength() > 2 * servers.Count)
        {
            return Fail(credentials);
        }

        foreach (var element in credentialsElement.EnumerateArray())
        {
            if (!HasExactProperties(element, [ServerIdProperty, KindProperty, ReferenceIdProperty, SecretProperty])
                || !TryReadGuid(element.GetProperty(ServerIdProperty), out var serverId)
                || !TryReadKind(element.GetProperty(KindProperty), out var kind)
                || !TryReadGuid(element.GetProperty(ReferenceIdProperty), out var referenceId)
                || ReadSecret(element.GetProperty(SecretProperty)) is not { } secret)
            {
                return Fail(credentials);
            }

            credentials.Add(new BackupCredential(serverId, kind, referenceId, secret));
        }

        var missingElement = root.GetProperty(MissingCredentialsProperty);
        if (missingElement.ValueKind != JsonValueKind.Array)
        {
            return Fail(credentials);
        }

        var missing = new List<(Guid, ServerCredentialKind)>();
        foreach (var element in missingElement.EnumerateArray())
        {
            if (!HasExactProperties(element, [ServerIdProperty, KindProperty])
                || !TryReadGuid(element.GetProperty(ServerIdProperty), out var serverId)
                || !TryReadKind(element.GetProperty(KindProperty), out var kind))
            {
                return Fail(credentials);
            }

            missing.Add((serverId, kind));
        }

        var knownHostsElement = root.GetProperty(KnownHostsProperty);
        var routedElement = root.GetProperty(RoutedKnownHostsProperty);
        if (knownHostsElement.ValueKind != JsonValueKind.Array
            || knownHostsElement.GetArrayLength() > MaxTrustEntries
            || routedElement.ValueKind != JsonValueKind.Array
            || routedElement.GetArrayLength() > MaxTrustEntries)
        {
            return Fail(credentials);
        }

        var knownHosts = JsonHostKeyTrustStore.ParseEntries(knownHostsElement, StrictOptions);
        var routedKnownHosts = JsonRoutedHostKeyTrustStore.ParseEntries(routedElement, StrictOptions);

        var settingsElement = root.GetProperty(SettingsProperty);
        if (!HasExactProperties(settingsElement, [NotificationsProperty, BackgroundProperty])
            || !TryReadBoolean(settingsElement.GetProperty(NotificationsProperty), out var notifications)
            || !TryReadBoolean(settingsElement.GetProperty(BackgroundProperty), out var background))
        {
            return Fail(credentials);
        }

        return new BackupPayload
        {
            CreatedAt = createdAt,
            AppVersion = appVersion,
            Servers = servers,
            Credentials = credentials,
            MissingCredentials = missing,
            KnownHosts = knownHosts,
            RoutedKnownHosts = routedKnownHosts,
            Settings = new PortableSettings(notifications, background),
        };
    }

    /// <summary>
    /// The domain rules of §2.1/§3 (C-5): every server valid by <paramref name="validator"/>, every enum defined,
    /// id non-empty and unique across direct and routed, normalization idempotent, key paths fully qualified and
    /// canonical, field limits; the exact credential set; referenced-only trust (H-4). Pure, in memory.
    /// </summary>
    public static bool Validate(BackupPayload payload, Core.Interfaces.IServerValidator validator)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(validator);

        var ids = new HashSet<Guid>();
        foreach (var server in payload.Servers)
        {
            if (!IsValidServer(server, validator) || !ids.Add(server.Id))
            {
                return false;
            }
        }

        if (!HasExactCredentialSet(payload))
        {
            return false;
        }

        var referenced = TrustReferenceSet.From(payload.Servers);
        return payload.KnownHosts.All(entry => referenced.Direct.Contains(entry.Endpoint))
            && payload.RoutedKnownHosts.All(entry => referenced.Routed.Contains(entry.Route));
    }

    /// <summary>The per-server rules shared by export (which must never produce a backup its own reader
    /// rejects) and the reader.</summary>
    public static bool IsValidServer(Server server, Core.Interfaces.IServerValidator validator)
    {
        if (server is null || server.Id == Guid.Empty)
        {
            return false;
        }

        if (!Enum.IsDefined(server.OperatingSystem)
            || !Enum.IsDefined(server.AuthenticationMethod)
            || (server.Route?.Jump is { } hop && !Enum.IsDefined(hop.AuthenticationMethod)))
        {
            return false;
        }

        if (!TextIsClean(server.Name, MaxTextLength)
            || !TextIsClean(server.Host, MaxTextLength)
            || !TextIsClean(server.Username, MaxTextLength)
            || !PathIsClean(server.PrivateKeyPath))
        {
            return false;
        }

        if (server.Route?.Jump is { } jump
            && (!TextIsClean(jump.Host, MaxTextLength)
                || !TextIsClean(jump.Username, MaxTextLength)
                || !PathIsClean(jump.PrivateKeyPath)))
        {
            return false;
        }

        if (!validator.Validate(server).IsValid)
        {
            return false;
        }

        try
        {
            return Core.Domain.ServerNormalizer.Normalize(server) == server;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool HasExactCredentialSet(BackupPayload payload)
    {
        var implied = new Dictionary<(Guid ServerId, ServerCredentialKind Kind), Guid>();
        foreach (var reference in payload.Servers.SelectMany(ServerCredentialReferences.All))
        {
            if (!implied.TryAdd((reference.ServerId, reference.Kind), reference.ReferenceId))
            {
                return false;
            }
        }

        var seen = new HashSet<(Guid, ServerCredentialKind)>();
        foreach (var credential in payload.Credentials)
        {
            if (!implied.TryGetValue((credential.ServerId, credential.Kind), out var referenceId)
                || referenceId != credential.ReferenceId
                || !seen.Add((credential.ServerId, credential.Kind)))
            {
                return false;
            }
        }

        foreach (var missing in payload.MissingCredentials)
        {
            if (!implied.ContainsKey(missing) || !seen.Add(missing))
            {
                return false;
            }
        }

        return seen.Count == implied.Count;
    }

    private static bool TextIsClean(string? value, int maxLength) =>
        value is not null && value.Length <= maxLength && !value.Any(char.IsControl);

    private static bool PathIsClean(string? path) =>
        path is null
        || (path.Length <= MaxPathLength
            && !path.Any(char.IsControl)
            && Core.Domain.ServerNormalizer.IsCanonicalKeyPath(path));

    private static SecretValue? ReadSecret(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        // The raw token, quotes included, straight from the pinned plaintext: no unescape, no copy.
        var raw = JsonMarshal.GetRawUtf8Value(element);
        if (raw.Length < 4 || raw[0] != (byte)'"' || raw[^1] != (byte)'"')
        {
            return null;
        }

        var hex = raw[1..^1];
        if (hex.Length % 2 != 0 || hex.Length > 2 * MaxSecretUtf8Bytes || hex.Contains((byte)'\\'))
        {
            return null;
        }

        var utf8 = GC.AllocateArray<byte>(hex.Length / 2, pinned: true);
        char[]? characters = null;
        try
        {
            for (var i = 0; i < utf8.Length; i++)
            {
                var high = HexValue(hex[2 * i]);
                var low = HexValue(hex[(2 * i) + 1]);
                if (high < 0 || low < 0)
                {
                    return null;
                }

                utf8[i] = (byte)((high << 4) | low);
            }

            characters = GC.AllocateArray<char>(StrictUtf8.GetCharCount(utf8), pinned: true);
            StrictUtf8.GetChars(utf8, characters);
            return new SecretValue(characters);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
            if (characters is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
            }
        }
    }

    // Lowercase only: the writer's form is canonical.
    private static int HexValue(byte digit) => digit switch
    {
        >= (byte)'0' and <= (byte)'9' => digit - '0',
        >= (byte)'a' and <= (byte)'f' => digit - 'a' + 10,
        _ => -1
    };

    private static bool TryReadGuid(JsonElement element, out Guid value)
    {
        value = Guid.Empty;
        return element.ValueKind == JsonValueKind.String
            && Guid.TryParseExact(element.GetString(), "D", out value)
            && value != Guid.Empty;
    }

    private static bool TryReadKind(JsonElement element, out ServerCredentialKind kind)
    {
        kind = 0;
        return element.ValueKind == JsonValueKind.String
            && BackupCredentialKindNames.TryParse(element.GetString(), out kind);
    }

    private static bool TryReadBoolean(JsonElement element, out bool value)
    {
        value = element.ValueKind == JsonValueKind.True;
        return element.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool TryGetExact(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Exactly these properties, exact case, each once (duplicates are already refused by the parser).</summary>
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

    /// <summary>No NUL or control character in any property name or string value (V9). Secret values are hex
    /// and are checked by <see cref="ReadSecret"/> without materializing them as strings.</summary>
    private static bool StringsAreClean(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Any(char.IsControl))
                    {
                        return false;
                    }

                    if (property.NameEquals(SecretProperty))
                    {
                        continue;
                    }

                    if (!StringsAreClean(property.Value))
                    {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (!StringsAreClean(item))
                    {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.String:
                return !element.GetString()!.Any(char.IsControl);

            default:
                return true;
        }
    }

    private static BackupPayload? Fail(List<BackupCredential> credentials)
    {
        DisposeAll(credentials);
        return null;
    }

    private static void DisposeAll(List<BackupCredential> credentials)
    {
        foreach (var credential in credentials)
        {
            credential.Secret.Dispose();
        }

        credentials.Clear();
    }

    private static BackupPayloadReadResult Invalid() => new(null, BackupError.InvalidContent);
}
