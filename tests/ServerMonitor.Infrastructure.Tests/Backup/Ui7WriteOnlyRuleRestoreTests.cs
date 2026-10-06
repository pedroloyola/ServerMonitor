using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Backup;

namespace ServerMonitor.Infrastructure.Tests.Backup;

/// <summary>
/// UI.7 H-UI7-1 counterproof (a), on the real JSON stores and the real backup engine over temp directories: servers that
/// break the new write-only rules (host with a scheme, a .pub key for the target or the jump host, a jump host that is the
/// target) still load from servers.json / routed-servers.json without quarantine, still pass the backup validation, and
/// still export and restore. The rules only refuse NEW writes.
/// </summary>
public sealed class Ui7WriteOnlyRuleRestoreTests
{
    private static readonly ReadOnlyMemory<char> Pass = BackupHarness.Passphrase.AsMemory();

    private static IReadOnlyList<Server> Legacy() =>
    [
        BackupHarness.Direct(21, "https://legacy.example.com"),
        BackupHarness.KeyServer(22, "db.example.com", @"C:\keys\id_22.pub"),
        BackupHarness.Routed(23, "10.0.0.5", "10.0.0.5"),
        BackupHarness.Routed(24, "10.0.0.6", "bastion.example.com") with
        {
            Route = new ServerRoute
            {
                Jump = new JumpHop
                {
                    Host = "bastion.example.com",
                    Port = 22,
                    Username = "jump",
                    AuthenticationMethod = AuthenticationMethod.SshKey,
                    PrivateKeyPath = @"C:\keys\jump_24.pub",
                    CredentialReferenceId = BackupHarness.Id(2024),
                }
            }
        },
    ];

    [Fact]
    public void EveryLegacyServer_BreaksAWriteRule_ButPassesTheStoredAndBackupValidation()
    {
        var validator = new ServerValidator();
        foreach (var server in Legacy())
        {
            var asWrite = new ServerInput
            {
                Name = server.Name,
                Host = server.Host,
                Port = server.Port,
                Username = server.Username,
                OperatingSystem = server.OperatingSystem,
                AuthenticationMethod = server.AuthenticationMethod,
                PrivateKeyPath = server.PrivateKeyPath,
                CredentialReferenceId = server.CredentialReferenceId,
                RefreshIntervalSeconds = server.RefreshIntervalSeconds,
                Route = server.Route
            };
            Assert.False(validator.Validate(asWrite).IsValid);
            Assert.True(validator.Validate(server).IsValid);
            Assert.True(BackupPayloadSerializer.IsValidServer(server, validator));
        }
    }

    [Fact]
    public async Task ServersJson_WithLegacyEntries_StillLoads_NothingQuarantined()
    {
        using var harness = new BackupHarness();
        await harness.SeedAsync(Legacy());
        harness.Reopen();

        var loaded = await harness.Servers.GetAllAsync();

        Assert.Equal(Legacy().Select(server => server.Id).Order(), loaded.Select(server => server.Id).Order());
    }

    [Fact]
    public async Task ABackup_WithLegacyEntries_ExportsAndRestores()
    {
        using var source = new BackupHarness();
        await source.SeedAsync(Legacy());
        source.Reopen();
        var path = source.OutPath();
        var export = await source.CreateService().ExportAsync(path, Pass, Pass);
        Assert.True(export.Succeeded, $"export failed: {export.Error}");

        using var target = new BackupHarness();
        await target.SeedAsync([BackupHarness.Direct(10, "other.example.com")]);
        target.Reopen();
        var bytes = await File.ReadAllBytesAsync(path);
        var copy = target.OutPath();
        await File.WriteAllBytesAsync(copy, bytes);
        var service = target.CreateService();
        var inspected = await service.InspectAsync(copy, Pass);
        Assert.True(inspected.Succeeded, $"inspect failed: {inspected.Error}");
        using var plan = inspected.Plan!;

        var applied = await service.ApplyAsync(plan);

        Assert.Equal(RestoreApplyOutcome.Completed, applied.Outcome);
        target.Reopen();
        var restored = await target.Servers.GetAllAsync();
        Assert.Equal(Legacy().Select(server => server.Id).Order(), restored.Select(server => server.Id).Order());
        Assert.Equal("https://legacy.example.com", restored.Single(server => server.Id == BackupHarness.Id(21)).Host);
    }
}
