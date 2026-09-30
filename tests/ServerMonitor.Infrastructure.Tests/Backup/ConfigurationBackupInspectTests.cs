using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Backup;
using ServerMonitor.Infrastructure.SSH;
using ServerMonitor.Infrastructure.SshConfig;

namespace ServerMonitor.Infrastructure.Tests.Backup;

// M14.6 §5.1 inspect: decode → strict parse → domain validation → re-key → summary. Never touches anything.
public sealed class ConfigurationBackupInspectTests
{
    private static readonly ReadOnlyMemory<char> Pass = BackupHarness.Passphrase.AsMemory();

    [Fact]
    public async Task Inspect_ValidBackup_SummarizesCurrentVersusBackup()
    {
        using var target = await BackupScenarios.TargetAsync();
        var path = await BackupScenarios.WriteSourceBackupAsync(target);
        var before = target.Snapshot();

        var result = await target.CreateService().InspectAsync(path, Pass);

        using var plan = result.Plan!;
        var summary = plan.Summary;
        Assert.Equal(new RestoreCounts(2, 2, 6, 2, 2), summary.Backup);
        Assert.Equal(new RestoreCounts(1, 0, 1, 2, 0), summary.Current);
        Assert.Equal(2, summary.DirectTrustedHostKeysToRemove);  // other + legacy
        Assert.Equal(0, summary.RoutedTrustedHostKeysToRemove);
        Assert.Equal(new PortableSettings(false, false), summary.BackupSettings);
        Assert.Equal(new PortableSettings(true, true), summary.CurrentSettings);
        Assert.Equal("1.2.0", summary.BackupAppVersion);
        Assert.Empty(summary.MissingCredentials);
        Assert.Empty(summary.KeyPathWarnings);
        Assert.Equal(before, target.Snapshot());
        target.AssertNoSecretsLeaked();
    }

    [Fact]
    public async Task Inspect_WrongPassphrase_IsWrongPassphraseOrDamaged_NothingChanged()
    {
        using var target = await BackupScenarios.TargetAsync();
        var path = await BackupScenarios.WriteSourceBackupAsync(target);
        var before = target.Snapshot();

        var result = await target.CreateService().InspectAsync(path, "correct horse battery stapler".AsMemory());

        Assert.Equal(BackupError.WrongPassphraseOrDamaged, result.Error);
        Assert.Null(result.Plan);
        Assert.Equal(before, target.Snapshot());
        target.AssertNoSecretsLeaked();
    }

    public static TheoryData<string> FileDamage() =>
    [
        "truncate-header", "truncate-ciphertext", "truncate-tag", "flip-magic", "flip-salt", "flip-nonce",
        "flip-length", "flip-ciphertext", "flip-tag", "trailing-byte", "empty", "tiny",
    ];

    [Theory]
    [MemberData(nameof(FileDamage))]
    public async Task Inspect_DamagedFile_IsRejected_NothingChanged(string damage)
    {
        using var target = await BackupScenarios.TargetAsync();
        var file = (await BackupScenarios.SourceBackupAsync()).File.ToArray();
        byte[] damaged = damage switch
        {
            "truncate-header" => file[..40],
            "truncate-ciphertext" => file[..(file.Length / 2)],
            "truncate-tag" => file[..^1],
            "trailing-byte" => [.. file, 0],
            "empty" => [],
            "tiny" => file[..81],
            _ => Flip(file, damage switch
            {
                "flip-magic" => 0,
                "flip-salt" => 20,
                "flip-nonce" => 55,
                "flip-length" => 62,
                "flip-ciphertext" => 70,
                _ => file.Length - 1,
            }),
        };
        var path = target.OutPath("damaged");
        await File.WriteAllBytesAsync(path, damaged);
        var before = target.Snapshot();

        var result = await target.CreateService().InspectAsync(path, Pass);

        Assert.Null(result.Plan);
        Assert.NotNull(result.Error);
        Assert.Equal(before, target.Snapshot());
    }

    [Fact]
    public async Task Inspect_FutureFormatVersion_IsIncompatible_WithoutAnyKdf()
    {
        using var target = await BackupScenarios.TargetAsync();
        var file = (await BackupScenarios.SourceBackupAsync()).File.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(BackupFileCodec.FormatVersionOffset), 2);
        var path = target.OutPath("future");
        await File.WriteAllBytesAsync(path, file);
        var kdf = 0;
        var codec = new BackupFileCodec(new BackupFileCodecSeams { OnKeyDerivation = _ => kdf++ });

        var result = await target.CreateService(codec: codec).InspectAsync(path, Pass);

        Assert.Equal(BackupError.IncompatibleVersion, result.Error);
        Assert.Equal(0, kdf);
    }

    [Fact]
    public async Task Inspect_PayloadSchemaVersion2_IsIncompatible_EvenWithUnknownFields()
    {
        using var target = await BackupScenarios.TargetAsync();
        var payload = await BackupScenarios.SourcePayloadAsync();
        payload["schemaVersion"] = 2;
        payload["somethingNew"] = true;
        var path = BackupScenarios.WriteEncrypted(target, BackupScenarios.Bytes(payload));

        var result = await target.CreateService().InspectAsync(path, Pass);

        Assert.Equal(BackupError.IncompatibleVersion, result.Error);
    }

    [Fact]
    public async Task Inspect_MissingFile_IsReadFailed()
    {
        using var target = await BackupScenarios.TargetAsync();

        var result = await target.CreateService().InspectAsync(target.OutPath("nope"), Pass);

        Assert.Equal(BackupError.ReadFailed, result.Error);
    }

    [Fact]
    public async Task Inspect_PassphraseOverTheMaximum_IsAPolicyProblem()
    {
        using var target = await BackupScenarios.TargetAsync();
        var path = await BackupScenarios.WriteSourceBackupAsync(target);

        var result = await target.CreateService().InspectAsync(path, new string('p', 257).AsMemory());

        Assert.Equal(BackupPassphraseProblem.TooLong, result.PassphraseProblem);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Inspect_WhileAJournalIsPending_IsRefused()
    {
        using var target = await BackupScenarios.TargetAsync();
        var path = await BackupScenarios.WriteSourceBackupAsync(target);
        Directory.CreateDirectory(target.JournalDirectory);

        var result = await target.CreateService().InspectAsync(path, Pass);

        Assert.Equal(BackupError.RestorePending, result.Error);
        Assert.Equal(target.JournalDirectory, result.PendingJournalDirectory);
    }

    // ------------------------------------------------ strict parse and domain rules (V2, V3, V9, H-4, C-5)

    private static readonly Guid Web = BackupScenarios.Web;
    private static readonly Guid Routed = BackupScenarios.RoutedPassword;
    private static readonly Guid Db = BackupScenarios.Db;

    private static readonly Dictionary<string, Func<JsonObject, byte[]>> Tampering = new()
    {
        // V2 strict overlay, every level
        ["unknown-root"] = root => Mutate(root, r => r["extra"] = 1),
        ["unknown-server"] = root => Mutate(root, r => Server(r, Web)["extra"] = 1),
        ["unknown-route"] = root => Mutate(root, r => Server(r, Routed)["route"]!["extra"] = 1),
        ["unknown-jump"] = root => Mutate(root, r => Server(r, Routed)["route"]!["jump"]!["extra"] = 1),
        ["unknown-credential"] = root => Mutate(root, r => r["credentials"]![0]!["extra"] = 1),
        ["unknown-missing"] = root => Mutate(root, r => r["missingCredentials"]!.AsArray().Add(new JsonObject { ["serverId"] = Web.ToString(), ["kind"] = "password", ["extra"] = 1 })),
        ["unknown-trust-entry"] = root => Mutate(root, r => r["knownHosts"]![0]!["extra"] = 1),
        ["unknown-trust-endpoint"] = root => Mutate(root, r => r["knownHosts"]![0]!["endpoint"]!["extra"] = 1),
        ["unknown-routed-trust"] = root => Mutate(root, r => r["routedKnownHosts"]![0]!["extra"] = 1),
        ["unknown-settings"] = root => Mutate(root, r => r["settings"]!["extra"] = true),
        ["case-variant-host"] = root => Mutate(root, r => Rename(Server(r, Web), "host", "Host")),
        ["case-variant-root"] = root => Mutate(root, r => Rename(r, "settings", "Settings")),
        ["duplicate-property"] = root => Text(root, json => ReplaceFirst(json, "\"name\":", "\"name\":\"dup\",\"name\":")),
        ["duplicate-root-property"] = root => Text(root, json => ReplaceFirst(json, "{", "{\"appVersion\":\"x\",")),
        ["enum-os-99"] = root => Mutate(root, r => Server(r, Web)["operatingSystem"] = 99),
        ["enum-auth-99"] = root => Mutate(root, r => Server(r, Web)["authenticationMethod"] = 99),
        ["enum-jump-auth-99"] = root => Mutate(root, r => Server(r, Routed)["route"]!["jump"]!["authenticationMethod"] = 99),
        ["number-as-string"] = root => Mutate(root, r => Server(r, Web)["port"] = "22"),
        ["empty-id"] = root => Mutate(root, r => Server(r, Web)["id"] = Guid.Empty.ToString()),
        ["duplicate-id-direct-vs-routed"] = root => Mutate(root, r => Server(r, Routed)["id"] = Web.ToString()),
        ["comment"] = root => Text(root, json => ReplaceFirst(json, "{", "{/*c*/")),
        ["trailing-comma"] = root => Text(root, json => json[..^1] + ",}"),
        ["bom"] = root => [0xEF, 0xBB, 0xBF, .. BackupScenarios.Bytes(root)],
        ["invalid-utf8"] = root => InvalidUtf8(BackupScenarios.Bytes(root)),
        ["not-json"] = _ => Encoding.UTF8.GetBytes("{\"schemaVersion\":1,"),
        ["missing-settings"] = root => Mutate(root, r => r.Remove("settings")),
        ["bad-created-at"] = root => Mutate(root, r => r["createdAt"] = "yesterday"),
        ["long-app-version"] = root => Mutate(root, r => r["appVersion"] = new string('1', 65)),

        // exact credential set
        ["extra-credential"] = root => Mutate(root, r => r["credentials"]!.AsArray().Add(WithReference(r["credentials"]![0]!, Guid.NewGuid()))),
        ["missing-implied-credential"] = root => Mutate(root, r => r["credentials"]!.AsArray().RemoveAt(0)),
        ["credential-wrong-reference"] = root => Mutate(root, r => r["credentials"]![0]!["referenceId"] = Guid.NewGuid().ToString()),
        ["credential-and-missing-both"] = root => Mutate(root, r => r["missingCredentials"]!.AsArray().Add(new JsonObject { ["serverId"] = (string)r["credentials"]![0]!["serverId"]!, ["kind"] = (string)r["credentials"]![0]!["kind"]! })),
        ["escaped-secret"] = root => Text(root, json => ReplaceFirst(json, "\"secretUtf8Hex\":\"5", "\"secretUtf8Hex\":\"\\u0035")),
        ["uppercase-secret-hex"] = root => Mutate(root, r => r["credentials"]![0]!["secretUtf8Hex"] = ((string)r["credentials"]![0]!["secretUtf8Hex"]!).ToUpperInvariant()),
        ["odd-secret-hex"] = root => Mutate(root, r => r["credentials"]![0]!["secretUtf8Hex"] = ((string)r["credentials"]![0]!["secretUtf8Hex"]!)[1..]),
        ["empty-secret"] = root => Mutate(root, r => r["credentials"]![0]!["secretUtf8Hex"] = ""),
        ["oversize-secret"] = root => Mutate(root, r => r["credentials"]![0]!["secretUtf8Hex"] = new string('a', 5122)),
        ["unknown-kind"] = root => Mutate(root, r => r["credentials"]![0]!["kind"] = "Password"),

        // validator, normalization idempotence, key paths, field limits (V3, V9)
        ["invalid-server-empty-name"] = root => Mutate(root, r => Server(r, Web)["name"] = ""),
        ["route-without-jump"] = root => Mutate(root, r => Server(r, Routed)["route"] = new JsonObject()),
        ["relative-key-path"] = root => Mutate(root, r => Server(r, Db)["privateKeyPath"] = "key"),
        ["drive-relative-key-path"] = root => Mutate(root, r => Server(r, Db)["privateKeyPath"] = "C:key"),
        ["whitespace-key-path"] = root => Mutate(root, r => Server(r, Db)["privateKeyPath"] = @"C:\keys\id_3 "),
        ["non-canonical-key-path"] = root => Mutate(root, r => Server(r, Db)["privateKeyPath"] = @"C:\keys\..\keys\id_3"),
        ["forward-slash-unc-key-path"] = root => Mutate(root, r => Server(r, Db)["privateKeyPath"] = "//host/share/k"),
        ["whitespace-host"] = root => Mutate(root, r => Server(r, Db)["host"] = " db.example.com"),
        ["whitespace-name"] = root => Mutate(root, r => Server(r, Web)["name"] = "web "),
        ["whitespace-jump-host"] = root => Mutate(root, r => Server(r, Routed)["route"]!["jump"]!["host"] = "bastion.example.com "),
        ["off-policy-interval"] = root => Mutate(root, r => Server(r, Web)["refreshIntervalSeconds"] = 45),
        ["host-257"] = root => Mutate(root, r => Server(r, Db)["host"] = new string('a', 257)),
        ["name-257"] = root => Mutate(root, r => Server(r, Web)["name"] = new string('a', 257)),
        ["nul-in-name"] = root => Mutate(root, r => Server(r, Web)["name"] = "we\u0000b"),
        ["control-in-username"] = root => Mutate(root, r => Server(r, Web)["username"] = "o\u0007ps"),
        ["oversize-key-path"] = root => Mutate(root, r => Server(r, Db)["privateKeyPath"] = @"C:\" + new string('a', 32_770)),

        // referenced-only trust (H-4)
        ["unreferenced-direct-trust"] = root => Mutate(root, r => r["knownHosts"]!.AsArray().Add(Clone(r["knownHosts"]![0]!, "endpoint", "stranger.example.com"))),
        ["target-endpoint-as-direct-trust"] = root => Mutate(root, r => r["knownHosts"]!.AsArray().Add(Clone(r["knownHosts"]![0]!, "endpoint", "10.0.0.5"))),
        ["unreferenced-routed-trust"] = root => Mutate(root, r => r["routedKnownHosts"]!.AsArray().Add(Clone(r["routedKnownHosts"]![0]!, "endpoint", "10.9.9.9"))),
        ["routed-trust-wrong-via"] = root => Mutate(root, r => r["routedKnownHosts"]![0]!["via"]!["host"] = "web.example.com"),
        ["duplicate-trust"] = root => Mutate(root, r => r["knownHosts"]!.AsArray().Add(r["knownHosts"]![0]!.DeepClone())),
    };

    public static TheoryData<string> TamperingCases() => [.. Tampering.Keys];

    [Theory]
    [MemberData(nameof(TamperingCases))]
    public async Task Inspect_InvalidContent_IsRejected_BeforeAnyWrite(string tampering)
    {
        using var target = await BackupScenarios.TargetAsync();
        var path = BackupScenarios.WriteEncrypted(target, Tampering[tampering](await BackupScenarios.SourcePayloadAsync()));
        var before = target.Snapshot();

        var result = await target.CreateService().InspectAsync(path, Pass);

        Assert.Null(result.Plan);
        Assert.Equal(BackupError.InvalidContent, result.Error);
        Assert.Equal(before, target.Snapshot());
        target.AssertNoSecretsLeaked();
    }

    [Fact]
    public async Task Inspect_UntouchedPayload_IsAccepted_ControlForTheTamperingCases()
    {
        using var target = await BackupScenarios.TargetAsync();
        var path = BackupScenarios.WriteEncrypted(target, BackupScenarios.Bytes(await BackupScenarios.SourcePayloadAsync()));

        var result = await target.CreateService().InspectAsync(path, Pass);

        Assert.True(result.Succeeded);
        result.Plan!.Dispose();
    }

    [Fact]
    public async Task Inspect_ReferencedKeyWithoutTrust_IsAccepted()
    {
        using var target = await BackupScenarios.TargetAsync();
        var payload = await BackupScenarios.SourcePayloadAsync();
        payload["knownHosts"]!.AsArray().RemoveAt(1); // web.example.com (sorted after bastion)
        var path = BackupScenarios.WriteEncrypted(target, BackupScenarios.Bytes(payload));

        var result = await target.CreateService().InspectAsync(path, Pass);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Plan!.Summary.Backup.DirectTrustedHostKeys);
        result.Plan.Dispose();
    }

    // The error log for rejected content carries codes only — never the content (C-6).
    [Fact]
    public async Task Inspect_InvalidContentWithAMarker_NeverReachesTheLog()
    {
        using var target = await BackupScenarios.TargetAsync();
        var payload = await BackupScenarios.SourcePayloadAsync();
        Server(payload, Web)["name"] = BackupHarness.SecretMarker + "in-a-name";
        Server(payload, Web)["extra"] = BackupHarness.SecretMarker + "in-an-unknown-member";
        var path = BackupScenarios.WriteEncrypted(target, BackupScenarios.Bytes(payload));

        var result = await target.CreateService().InspectAsync(path, Pass);

        Assert.Equal(BackupError.InvalidContent, result.Error);
        Assert.Contains(target.Log.Entries, entry => entry.Contains("InvalidContent", StringComparison.Ordinal));
        target.AssertNoSecretsLeaked();
    }

    // ------------------------------------------------ key paths (V1, C-7, spec §6.6)

    public static TheoryData<string> NetworkPaths() =>
    [
        @"\\host\share\k",
        @"\\?\UNC\host\share\k",
        @"\\.\UNC\host\share\k",
    ];

    [Theory]
    [MemberData(nameof(NetworkPaths))]
    public async Task Inspect_NetworkKeyPath_IsRestorableButFlaggedNotChecked_AndNeverProbed(string keyPath)
    {
        using var target = await BackupScenarios.TargetAsync();
        var payload = await BackupScenarios.SourcePayloadAsync();
        Server(payload, Db)["privateKeyPath"] = keyPath;
        var path = BackupScenarios.WriteEncrypted(target, BackupScenarios.Bytes(payload));
        var touched = new List<string>();
        var spy = new LocalKeyFileAccess(
            root => { touched.Add("drive:" + root); return DriveType.Fixed; },
            p => { touched.Add("attributes:" + p); return FileAttributes.Directory; },
            p => { touched.Add("open:" + p); throw new InvalidOperationException(); },
            p => { touched.Add("describe:" + p); return new LocalSshKeyFileMetadata(LocalSshKeyPathKind.RegularFile, false, 1); });

        var result = await target.CreateService(keyFiles: spy).InspectAsync(path, Pass);

        using var plan = result.Plan!;
        var warning = Assert.Single(plan.Summary.KeyPathWarnings);
        Assert.Equal(new KeyPathWarning(Db, "direct-3", false, KeyPathStatus.NotChecked), warning);
        Assert.DoesNotContain(touched, entry => entry.Contains("host", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Inspect_MappedNetworkDriveKeyPath_IsNotChecked_AndNeverDescribed()
    {
        using var target = await BackupScenarios.TargetAsync();
        var path = await BackupScenarios.WriteSourceBackupAsync(target);
        var described = 0;
        var network = new LocalKeyFileAccess(
            _ => DriveType.Network,
            _ => { described++; return FileAttributes.Directory; },
            _ => throw new InvalidOperationException(),
            _ => { described++; return LocalSshKeyFileMetadata.Missing; });

        var result = await target.CreateService(keyFiles: network).InspectAsync(path, Pass);

        using var plan = result.Plan!;
        Assert.Equal(3, plan.Summary.KeyPathWarnings.Count); // db, routed-4 target, routed-2 jump
        Assert.All(plan.Summary.KeyPathWarnings, warning => Assert.Equal(KeyPathStatus.NotChecked, warning.Status));
        Assert.Equal(0, described);
    }

    [Fact]
    public async Task Inspect_LocalKeyPaths_MissingAndReparse_AreFlagged()
    {
        using var target = await BackupScenarios.TargetAsync();
        var path = await BackupScenarios.WriteSourceBackupAsync(target);
        var local = new LocalKeyFileAccess(
            _ => DriveType.Fixed,
            _ => FileAttributes.Directory,
            _ => throw new InvalidOperationException(),
            p => p.EndsWith("id_3", StringComparison.Ordinal)
                ? LocalSshKeyFileMetadata.Missing
                : new LocalSshKeyFileMetadata(LocalSshKeyPathKind.RegularFile, IsReparsePoint: p.EndsWith("id_4", StringComparison.Ordinal), 10));

        var result = await target.CreateService(keyFiles: local).InspectAsync(path, Pass);

        using var plan = result.Plan!;
        Assert.Contains(new KeyPathWarning(Db, "direct-3", false, KeyPathStatus.Missing), plan.Summary.KeyPathWarnings);
        Assert.Contains(new KeyPathWarning(BackupScenarios.RoutedJumpPassword, "routed-4", false, KeyPathStatus.Unsupported), plan.Summary.KeyPathWarnings);
        Assert.Equal(2, plan.Summary.KeyPathWarnings.Count);
    }

    // ------------------------------------------------ helpers

    private static JsonObject Server(JsonObject root, Guid id) => BackupScenarios.ServerNode(root, id);

    private static byte[] Mutate(JsonObject root, Action<JsonObject> mutation)
    {
        mutation(root);
        return BackupScenarios.Bytes(root);
    }

    private static byte[] Text(JsonObject root, Func<string, string> mutation) =>
        Encoding.UTF8.GetBytes(mutation(root.ToJsonString()));

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0, oldValue);
        return string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }

    private static void Rename(JsonNode node, string from, string to)
    {
        var obj = node.AsObject();
        var value = obj[from]!.DeepClone();
        obj.Remove(from);
        obj[to] = value;
    }

    private static JsonNode WithReference(JsonNode credential, Guid referenceId)
    {
        var copy = credential.DeepClone();
        copy["referenceId"] = referenceId.ToString();
        return copy;
    }

    private static JsonNode Clone(JsonNode entry, string endpointProperty, string host)
    {
        var copy = entry.DeepClone();
        copy[endpointProperty]!["host"] = host;
        return copy;
    }

    private static byte[] InvalidUtf8(byte[] bytes)
    {
        var index = Encoding.UTF8.GetString(bytes).IndexOf("web.example.com", StringComparison.Ordinal);
        bytes[index] = 0xFF;
        return bytes;
    }

    private static byte[] Flip(byte[] file, int offset)
    {
        file[offset] ^= 0x01;
        return file;
    }
}
