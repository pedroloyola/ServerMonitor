using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.Infrastructure.Tests.Backup;

// The stores' restore participation (M14.6 §3, §5.2, §5.5): restore-only writes need the gate's token, the
// render statics are byte-identical to a normal save, and the gated credential decorator gates writes only.
public sealed class BackupStoreParticipationTests
{
    [Fact]
    public async Task RestoreOnlyWrites_RequireTheCurrentRestoreToken()
    {
        using var harness = new BackupHarness();
        var token = (await harness.Gate.BeginRestoreAsync(TimeSpan.FromSeconds(1)))!;
        harness.Gate.Release(token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Repository.ReplaceFileForRestoreAsync(token, false, [1], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.DirectTrust.ReplaceForRestoreAsync(token, [1], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RoutedTrust.ReplaceForRestoreAsync(token, [1], CancellationToken.None));
        Assert.False(File.Exists(harness.ServerOptions.FilePath));
        Assert.False(File.Exists(harness.TrustOptions.FilePath));
    }

    [Fact]
    public async Task TrustRender_IsByteIdenticalToANormalSave()
    {
        using var harness = new BackupHarness();
        var endpoint = SshEndpoint.Create("web.example.com", 22);
        var identity = BackupHarness.DirectTrustEntry("web.example.com").Identity;
        await harness.DirectTrust.TrustAsync(endpoint, identity);
        var route = SshRoute.Create(SshEndpoint.Create("bastion", 22), SshEndpoint.Create("10.0.0.5", 22));
        await harness.RoutedTrust.TrustAsync(route, identity);

        var direct = await harness.DirectTrust.ExportAllAsync(CancellationToken.None);
        var routed = await harness.RoutedTrust.ExportAllAsync(CancellationToken.None);

        Assert.Equal(JsonHostKeyTrustStore.Render(direct), await File.ReadAllBytesAsync(harness.TrustOptions.FilePath));
        Assert.Equal(JsonRoutedHostKeyTrustStore.Render(routed), await File.ReadAllBytesAsync(harness.RoutedTrustOptions.FilePath));
    }

    [Fact]
    public async Task ReplaceForRestore_DropsTheTrustCache()
    {
        using var harness = new BackupHarness();
        await harness.DirectTrust.TrustAsync(SshEndpoint.Create("old.example.com", 22), BackupHarness.DirectTrustEntry("x").Identity);
        var token = (await harness.Gate.BeginRestoreAsync(TimeSpan.FromSeconds(1)))!;

        await harness.DirectTrust.ReplaceForRestoreAsync(token, JsonHostKeyTrustStore.Render([BackupHarness.DirectTrustEntry("new.example.com")]), CancellationToken.None);

        Assert.Null(await harness.DirectTrust.GetAsync(SshEndpoint.Create("old.example.com", 22)));
        Assert.NotNull(await harness.DirectTrust.GetAsync(SshEndpoint.Create("new.example.com", 22)));
    }

    [Fact]
    public async Task RenderReplacement_PutsDirectAndRoutedInTheirOwnFiles_NothingElse()
    {
        using var harness = new BackupHarness();
        await harness.SeedAsync([BackupHarness.Direct(9, "stale.example.com")]);
        var servers = new[] { BackupHarness.Direct(1, "a"), BackupHarness.Routed(2, "10.0.0.5", "b") };
        var (direct, routed) = JsonServerRepository.RenderReplacement(servers);
        var token = (await harness.Gate.BeginRestoreAsync(TimeSpan.FromSeconds(1)))!;

        await harness.Repository.ReplaceFileForRestoreAsync(token, true, routed, CancellationToken.None);
        await harness.Repository.ReplaceFileForRestoreAsync(token, false, direct, CancellationToken.None);

        var loaded = await harness.Repository.ReadForVerifyAsync(CancellationToken.None);
        Assert.Equal(servers.OrderBy(s => s.Id), loaded!.OrderBy(s => s.Id));
    }

    [Fact]
    public async Task GatedCredentialStore_GatesWritesAndDeletes_NotReads()
    {
        using var harness = new BackupHarness();
        var reference = CredentialReference.Create(Guid.NewGuid(), ServerCredentialKind.Password);
        harness.Raw.Seed(reference, "value");
        var token = (await harness.Gate.BeginRestoreAsync(TimeSpan.FromSeconds(1)))!;
        using var secret = new SecretValue("new");

        using (var read = await harness.Credentials.ReadAsync(reference))
        {
            Assert.NotNull(read);
        }

        await Assert.ThrowsAsync<ConfigurationLockedException>(() => harness.Credentials.WriteAsync(reference, secret));
        await Assert.ThrowsAsync<ConfigurationLockedException>(() => harness.Credentials.DeleteAsync(reference));
        Assert.Equal("value", harness.Raw.Get(reference));
        harness.Gate.Release(token);
    }

    // Spec §5.2 step 3 / cp 10: a crash between the two server files (no journal to help) never makes a server
    // that is routed in the old OR the new configuration load as DIRECT. Exercised for BOTH write orders: the
    // guarantee comes from the repository's claim rule (an id the routed file claims never loads direct), so it
    // holds whichever file lands first; the engine's routed-first order is defence in depth.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task CrashBetweenTheServerFiles_WithoutAJournal_NeverLoadsARoutedServerAsDirect(bool currentlyRouted, bool routedFileFirst)
    {
        using var harness = new BackupHarness();
        var current = currentlyRouted ? BackupHarness.Routed(5, "10.0.0.5", "bastion") : BackupHarness.Direct(5, "10.0.0.5");
        var restored = currentlyRouted ? BackupHarness.Direct(5, "10.0.0.5") : BackupHarness.Routed(5, "10.0.0.5", "bastion");
        await harness.SeedAsync([current]);
        var (direct, routed) = JsonServerRepository.RenderReplacement([restored]);
        var token = (await harness.Gate.BeginRestoreAsync(TimeSpan.FromSeconds(1)))!;

        // The process dies after the first of the two server files.
        await harness.Repository.ReplaceFileForRestoreAsync(token, routedFileFirst, routedFileFirst ? routed : direct, CancellationToken.None);
        harness.Reopen();

        var loaded = await harness.Repository.GetAllAsync();
        Assert.DoesNotContain(loaded, server => server.Id == BackupHarness.Id(5) && server.Route is null);
    }

    // Atlas-1 probe 1, ported (Cortex-1): a task started INSIDE a lease, released only after the lease is disposed
    // and a restore holds (or has sealed) the gate, must not write through the REAL ServerService.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATaskCapturedInsideALease_CannotWriteThroughTheRealServerService_AfterTheDrain(bool seal)
    {
        using var target = await BackupScenarios.TargetAsync();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outer = target.Gate.EnterWrite();
        var child = Task.Run(async () =>
        {
            await resume.Task.ConfigureAwait(false);
            return await Record.ExceptionAsync(() => target.Servers.HideAsync(BackupScenarios.TargetOther));
        });
        outer.Dispose();
        var token = await target.Gate.BeginRestoreAsync(Timeout.InfiniteTimeSpan);
        Assert.NotNull(token);
        if (seal)
        {
            target.Gate.Seal(token);
        }

        var before = target.Snapshot();
        resume.SetResult();

        Assert.IsType<ConfigurationLockedException>(await child.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(before, target.Snapshot());
    }
}
