using System.Text;
using System.Text.Json.Nodes;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Backup;

namespace ServerMonitor.Infrastructure.Tests.Backup;

/// <summary>Deterministic "machines" for the engine tests.</summary>
internal static class BackupScenarios
{
    public static readonly Guid Web = BackupHarness.Id(1);
    public static readonly Guid RoutedPassword = BackupHarness.Id(2);
    public static readonly Guid Db = BackupHarness.Id(3);
    public static readonly Guid RoutedJumpPassword = BackupHarness.Id(4);
    public static readonly Guid TargetOther = BackupHarness.Id(10);

    private static readonly Lazy<Task<(byte[] File, byte[] Plaintext)>> SourceBackupLazy = new(CreateSourceBackupAsync);

    /// <summary>All four credential kinds, direct + routed, one key server without trust, orphan trust on both
    /// sides, non-default settings.</summary>
    public static IReadOnlyList<Server> SourceServers() =>
    [
        BackupHarness.Direct(1, "web.example.com"),
        BackupHarness.Routed(2, "10.0.0.5", "bastion.example.com"),
        BackupHarness.KeyServer(3, "db.example.com", @"C:\keys\id_3"),
        BackupHarness.Routed(4, "10.0.0.6", "bastion.example.com") with
        {
            AuthenticationMethod = AuthenticationMethod.SshKey,
            PrivateKeyPath = @"C:\keys\id_4",
            Route = new ServerRoute
            {
                Jump = new JumpHop
                {
                    Host = "bastion.example.com",
                    Port = 22,
                    Username = "jump",
                    AuthenticationMethod = AuthenticationMethod.Password,
                    CredentialReferenceId = BackupHarness.Id(2004),
                }
            }
        },
    ];

    public static async Task<BackupHarness> SourceAsync()
    {
        var harness = new BackupHarness();
        await harness.SeedAsync(SourceServers());
        harness.SeedTrust(
            [
                BackupHarness.DirectTrustEntry("web.example.com", seed: 1),
                BackupHarness.DirectTrustEntry("bastion.example.com", seed: 2),
                BackupHarness.DirectTrustEntry("old.example.com", seed: 3),
            ],
            [
                BackupHarness.RoutedTrustEntry("bastion.example.com", "10.0.0.5", seed: 4),
                BackupHarness.RoutedTrustEntry("bastion.example.com", "10.0.0.6", seed: 5),
                BackupHarness.RoutedTrustEntry("bastion.example.com", "10.9.9.9", seed: 6),
            ]);
        harness.SeedSettings(notifications: false, background: false, noticeShown: true);
        return harness;
    }

    /// <summary>A different machine: one direct server, one used and one unrelated trust entry, defaults.</summary>
    public static async Task<BackupHarness> TargetAsync()
    {
        var harness = new BackupHarness();
        await harness.SeedAsync([BackupHarness.Direct(10, "other.example.com")]);
        harness.SeedTrust(
            [
                BackupHarness.DirectTrustEntry("other.example.com", seed: 7),
                BackupHarness.DirectTrustEntry("legacy.example.com", seed: 8),
            ],
            []);
        harness.SeedSettings(notifications: true, background: true, noticeShown: false);
        return harness;
    }

    /// <summary>One real export of the source machine, shared by the tests that only need a valid backup.</summary>
    public static Task<(byte[] File, byte[] Plaintext)> SourceBackupAsync() => SourceBackupLazy.Value;

    public static async Task<string> WriteSourceBackupAsync(BackupHarness harness, string name = "backup.serveralyzer-backup")
    {
        var path = harness.OutPath(name);
        await File.WriteAllBytesAsync(path, (await SourceBackupAsync()).File);
        return path;
    }

    /// <summary>Encrypts <paramref name="plaintext"/> exactly like an export (valid crypto, chosen content).</summary>
    public static string WriteEncrypted(BackupHarness harness, byte[] plaintext, string name = "crafted.serveralyzer-backup")
    {
        var path = harness.OutPath(name);
        File.WriteAllBytes(path, BackupHarness.FastCodec.Encrypt(plaintext, BackupHarness.Passphrase).File!);
        return path;
    }

    public static async Task<JsonObject> SourcePayloadAsync() =>
        JsonNode.Parse((await SourceBackupAsync()).Plaintext)!.AsObject();

    public static JsonObject ServerNode(JsonObject root, Guid id) =>
        root["servers"]!.AsArray().Select(node => node!.AsObject()).Single(node => (string)node["id"]! == id.ToString());

    public static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    private static async Task<(byte[] File, byte[] Plaintext)> CreateSourceBackupAsync()
    {
        using var source = await SourceAsync();
        var path = source.OutPath();
        var result = await source.CreateService().ExportAsync(
            path,
            BackupHarness.Passphrase.AsMemory(),
            BackupHarness.Passphrase.AsMemory());
        Assert.True(result.Succeeded, $"source export failed: {result.Error} {result.PassphraseProblem}");
        var file = await File.ReadAllBytesAsync(path);
        using var decoded = BackupHarness.FastCodec.Decrypt(file, BackupHarness.Passphrase);
        return (file, decoded.Plaintext!.Span.ToArray());
    }
}
