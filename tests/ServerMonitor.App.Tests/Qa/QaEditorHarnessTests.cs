using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Qa;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.TestSupport;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Persistence;


namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// UI.7 B-23: the Debug-only <c>--qa-editor</c> harness. The strict parser refuses every malformed or orphan modifier
/// (exit 3, never another composition); layered on the REAL root (through <see cref="IsolatedAppComposition"/>) plus the
/// launch isolation, every resolved path stays under the QA roots, the credential store is never the Credential Manager,
/// the SSH service is the scripted one and the profile/server/trust services are the real ones; the catalogue is closed.
/// </summary>
public sealed class QaEditorHarnessTests
{
    private const string Exe = "ServerMonitor.App.exe";

    [Theory]
    [InlineData("--qa-editor", "--qa-backup", "ok")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-seed=routed", "--qa-start=editor-edit:1")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-ssh=hostkey-unknown-target:held", "--qa-start=editor-add")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-ssh=ok-linux:held-at-auth", "--qa-start=editor-add")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-save=locked", "--qa-start=editor-import")]
    public void WellFormedLaunches_AreAllowed(params string[] args) =>
        Assert.Null(QaStartupIsolation.LaunchRefusal([Exe, .. args]));

    [Theory]
    [InlineData("--qa-editor")] // no --qa-backup
    [InlineData("--qa-health", "--qa-editor-seed=direct")] // orphan modifier
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-seed=Direct")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-seed=other")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-ssh=ok")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-ssh=ok-linux:HELD")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-ssh=ok-linux:held-at-AUTH")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-ssh=ok-linux:held:held-at-auth")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-ssh=ok-linux:held-at-auth:held")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-ssh=:held-at-auth")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-save=maybe")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-seed=direct", "--qa-editor-seed=routed")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-start=editor-edit:1")] // nothing seeded
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-seed=direct", "--qa-start=editor-edit:2")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-start=overview")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-seed=direct")] // the seed is written by --qa-start
    [InlineData("--QA-EDITOR", "--qa-backup", "ok")]
    public void MalformedOrOrphanLaunches_AreRefused(params string[] args) =>
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, .. args]));

    [Fact]
    public void OnTheRealRoot_EveryPathStaysInTheQaRoots_AndOnlyTheSshServiceIsScripted()
    {
        using var app = Compose([Exe, "--qa-editor", "--qa-backup", "ok"]);
        using var provider = app.BuildProvider();

        // Every path the editor can read or write (servers, routed servers, both trust stores, the SSH profile, the key
        // picker) resolves under the QA root - never the stand-in real profile of the isolated composition.
        string[] editorPaths =
        [
            "ServerStorageOptions.FilePath", "ServerStorageOptions.RoutedFilePath", "HostKeyTrustStorageOptions.FilePath",
            "RoutedHostKeyTrustStorageOptions.FilePath", "SshConfigFileImportSource.ConfigPath", "LocalSshKeyDiscovery.SshDirectory",
            "PrivateKeyFilePicker.UserProfile"
        ];
        var resolved = QaStartupIsolation.ResolvedPaths(provider).Where(pair => editorPaths.Contains(pair.Name)).ToList();
        Assert.Equal(editorPaths.Length, resolved.Count);
        Assert.All(resolved, pair => Assert.Null(QaStartupIsolation.Violation(pair.Path, [QaTestRoots.Root], [app.DataDirectory, app.UserProfile])));
        Assert.IsType<QaScriptedSshConnectionService>(provider.GetRequiredService<ISshConnectionService>());
        Assert.IsType<ServerProfileService>(provider.GetRequiredService<IServerProfileService>());
        Assert.IsType<ServerService>(provider.GetRequiredService<IServerService>());
        Assert.IsType<JsonHostKeyTrustStore>(provider.GetRequiredService<IHostKeyTrustStore>());
        Assert.IsType<JsonRoutedHostKeyTrustStore>(provider.GetRequiredService<IRoutedHostKeyTrustStore>());
        Assert.IsType<QaInMemoryCredentialStore>(provider.GetRequiredService<ServerMonitor.Infrastructure.Security.UngatedCredentialStore>().Store);
        Assert.IsType<QaMonitoringEngine>(provider.GetRequiredService<IMonitoringEngine>());
        // The harness adds NO hosted service: the seed is written by the --qa-start step on the UI thread (runtime smoke:
        // as a startup hosted service the Edit launch never reached the editor).
        Assert.NotNull(provider.GetRequiredService<QaEditorSeed>());
        Assert.Equal(app.HostedBeforeEditor, app.Services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)));
        Assert.StartsWith(QaTestRoots.Root, provider.GetRequiredService<ServerStorageOptions>().FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("locked")]
    public async Task ASaveFailure_WrapsOnlyTheEditorsProfileService(string failure)
    {
        using var app = Compose([Exe, "--qa-editor", "--qa-backup", "ok", "--qa-editor-save=" + failure]);
        using var provider = app.BuildProvider();
        var profiles = Assert.IsType<QaFailingProfileService>(provider.GetRequiredService<IServerProfileService>());

        var add = profiles.AddAsync(new ServerProfileInput { Configuration = new ServerInput { Name = "x" }, CredentialChange = CredentialChange.Clear });

        if (failure == "locked")
        {
            await Assert.ThrowsAsync<ConfigurationLockedException>(() => add);
        }
        else
        {
            Assert.False((await add).Succeeded);
        }
    }

    [Theory]
    [InlineData("direct", false)]
    [InlineData("routed", true)]
    [InlineData("password", false)]
    public async Task TheSeed_IsWrittenThroughTheRealProfilePath_OnTheQaRoot(string seed, bool routed)
    {
        using var world = new SeedWorld();
        var qaSeed = new QaEditorSeed(seed, world.Profiles, world.Root);
        await qaSeed.EnsureWrittenAsync();
        await qaSeed.EnsureWrittenAsync(); // once

        var server = Assert.Single(await world.Servers.GetAllAsync());
        Assert.Equal(routed, server.Route is not null);
        Assert.Equal(seed == "password" || routed ? 1 : 0, world.Credentials.Count);
    }

    [Fact]
    public void TheCatalogue_IsClosed_AndEveryOutcomeAnswersForTheMatchingForm()
    {
        Assert.Equal(24, QaScriptedSshConnectionService.Outcomes.Count); // UI.7B: + hostkey-unknown-jump-then-target
        Assert.Equal(QaScriptedSshConnectionService.Outcomes.Count, QaScriptedSshConnectionService.Outcomes.Distinct().Count());
        foreach (var outcome in QaScriptedSshConnectionService.Outcomes.Where(o => o is not ("cancelled-at-auth" or "unexpected")))
        {
            var routed = outcome.Contains("jump", StringComparison.Ordinal) || outcome.Contains("target", StringComparison.Ordinal)
                || outcome == "tunnel";
            var service = new QaScriptedSshConnectionService(outcome, held: false, new NoTrust(), new NoRoutedTrust());
            var result = service.TestConnectionAsync(Request(routed)).GetAwaiter().GetResult();
            Assert.NotEqual(SshConnectionErrorCode.InvalidConfiguration, result.ErrorCode);
        }
    }

    [Fact]
    public async Task HostKeyUnknown_LastsUntilTheRealStoreTrustsTheKey_ThenTheRetestSucceeds()
    {
        using var world = new SeedWorld();
        var service = new QaScriptedSshConnectionService("hostkey-unknown-direct", held: false, world.Trust, new NoRoutedTrust());
        var stages = new List<SshConnectionStage>();

        var first = await service.TestConnectionAsync(Request(routed: false, new Progress(stages)));
        Assert.Equal(ServerConnectionState.HostKeyUnknown, first.State);
        Assert.Equal([SshConnectionStage.PortReachable], stages);

        await world.Trust.TrustAsync(first.HostKeyEndpoint!, first.PresentedHostKey!);
        stages.Clear();
        var second = await service.TestConnectionAsync(Request(routed: false, new Progress(stages)));

        Assert.True(second.IsSuccess);
        Assert.Equal(
            [SshConnectionStage.PortReachable, SshConnectionStage.HostKeyVerified, SshConnectionStage.Authenticated, SshConnectionStage.OperatingSystemIdentified],
            stages);
    }

    /// <summary>UI.7B: the two-step outcome asks for the jump's key, then the target's, consulting the stores each time.</summary>
    [Fact]
    public async Task JumpThenTarget_AsksTheJumpFirst_ThenTheTarget_ThenSucceeds()
    {
        var direct = new MemoryTrust();
        var routed = new MemoryRoutedTrust();
        var service = new QaScriptedSshConnectionService("hostkey-unknown-jump-then-target", held: false, direct, routed);

        var first = await service.TestConnectionAsync(Request(routed: true));
        Assert.Equal((ServerConnectionState.HostKeyUnknown, SshHostKeyHop.Jump), (first.State, first.HostKeyHop));

        await direct.TrustAsync(first.HostKeyEndpoint!, first.PresentedHostKey!);
        var second = await service.TestConnectionAsync(Request(routed: true));
        Assert.Equal((ServerConnectionState.HostKeyUnknown, SshHostKeyHop.Target), (second.State, second.HostKeyHop));

        await routed.TrustAsync(second.HostKeyRoute!, second.PresentedHostKey!);
        Assert.True((await service.TestConnectionAsync(Request(routed: true))).IsSuccess);
    }

    private sealed class MemoryTrust : IHostKeyTrustStore
    {
        private readonly Dictionary<SshEndpoint, HostKeyIdentity> _keys = [];

        public Task<TrustedHostKey?> GetAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) =>
            Task.FromResult(_keys.TryGetValue(endpoint, out var key) ? new TrustedHostKey { Endpoint = endpoint, Identity = key } : null);

        public Task TrustAsync(SshEndpoint endpoint, HostKeyIdentity identity, CancellationToken cancellationToken = default)
        {
            _keys[endpoint] = identity;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) => Task.FromResult(_keys.Remove(endpoint));
    }

    private sealed class MemoryRoutedTrust : IRoutedHostKeyTrustStore
    {
        private readonly Dictionary<SshRoute, HostKeyIdentity> _keys = [];

        public Task<TrustedRoutedHostKey?> GetAsync(SshRoute route, CancellationToken cancellationToken = default) =>
            Task.FromResult(_keys.TryGetValue(route, out var key) ? new TrustedRoutedHostKey { Route = route, Identity = key } : null);

        public Task TrustAsync(SshRoute route, HostKeyIdentity identity, CancellationToken cancellationToken = default)
        {
            _keys[route] = identity;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(SshRoute route, CancellationToken cancellationToken = default) => Task.FromResult(_keys.Remove(route));
    }

    [Fact]
    public async Task AHeldTest_WaitsForTheReleaseSignal_OrForCancellation_NeverAClock()
    {
        var service = new QaScriptedSshConnectionService("ok-linux", held: true, new NoTrust(), new NoRoutedTrust());

        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = service.TestConnectionAsync(Request(routed: false), cancellation.Token);
            Assert.False(cancelled.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        }

        var released = service.TestConnectionAsync(Request(routed: false));
        Assert.False(released.IsCompleted);
        using (var signal = EventWaitHandle.OpenExisting(QaScriptedSshConnectionService.ReleaseEventName))
        {
            signal.Set();
        }

        Assert.True((await released).IsSuccess);
    }

    [Theory]
    [InlineData("ok-linux:held-at-auth", "ok-linux", false, true)]
    [InlineData("ok-linux:held", "ok-linux", true, false)]
    [InlineData("fail-auth", "fail-auth", false, false)]
    public void TheSshScript_IsParsedStrictly(string value, string outcome, bool held, bool heldAtAuth) =>
        Assert.Equal((outcome, held, heldAtAuth), QaEditorComposition.SshScript([Exe, "--qa-editor", "--qa-editor-ssh=" + value]));

    // UI.7 final c1 (Prism R4): held-at-auth shows the test MID-way - port and host key done, authentication running, the
    // rest pending - and reports the remaining stages only when released (no clock).
    [Fact]
    public async Task ATestHeldAtAuth_ReportsTheFirstTwoStages_ThenTheRestOnRelease()
    {
        var service = new QaScriptedSshConnectionService("ok-linux", held: false, new NoTrust(), new NoRoutedTrust(), heldAtAuth: true);
        var stages = new List<SshConnectionStage>();

        var test = service.TestConnectionAsync(Request(routed: false, new Progress(stages)));
        Assert.False(test.IsCompleted);
        Assert.Equal([SshConnectionStage.PortReachable, SshConnectionStage.HostKeyVerified], stages);

        using (var signal = EventWaitHandle.OpenExisting(QaScriptedSshConnectionService.ReleaseEventName))
        {
            signal.Set();
        }

        Assert.True((await test).IsSuccess);
        Assert.Equal(
            [SshConnectionStage.PortReachable, SshConnectionStage.HostKeyVerified, SshConnectionStage.Authenticated, SshConnectionStage.OperatingSystemIdentified],
            stages);
    }

    private static ComposedHarness Compose(IReadOnlyList<string> args)
    {
        var app = new IsolatedAppComposition();
        QaStartupIsolation.Apply(app.Services, Path.Combine(QaTestRoots.Root, "isolated", "editor-" + Guid.NewGuid().ToString("N")), rerootSshProfile: true);
        var hosted = app.Services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService));
        QaEditorComposition.Apply(app.Services, args);
        return new ComposedHarness(app, hosted);
    }

    private sealed class ComposedHarness(IsolatedAppComposition app, int hostedBeforeEditor) : IDisposable
    {
        public int HostedBeforeEditor { get; } = hostedBeforeEditor;

        public Microsoft.Extensions.DependencyInjection.ServiceCollection Services => app.Services;

        public string DataDirectory => app.DataDirectory;

        public string UserProfile => app.UserProfile;

        public ServiceProvider BuildProvider() => app.BuildProvider();

        public void Dispose() => app.Dispose();
    }

    private static SshConnectionRequest Request(bool routed, IProgress<SshConnectionStage>? progress = null) => new()
    {
        Server = new Server
        {
            Id = Guid.NewGuid(),
            Name = "qa",
            Host = "192.0.2.10",
            Port = 22,
            Username = "monitor",
            AuthenticationMethod = AuthenticationMethod.SshKey,
            PrivateKeyPath = "id",
            Route = routed
                ? new ServerRoute { Jump = new JumpHop { Host = "bastion.example.com", Port = 22, Username = "admin", AuthenticationMethod = AuthenticationMethod.Password } }
                : null
        },
        StageProgress = progress
    };

    private sealed class Progress(List<SshConnectionStage> stages) : IProgress<SshConnectionStage>
    {
        public void Report(SshConnectionStage value) => stages.Add(value);
    }

    private sealed class NoTrust : IHostKeyTrustStore
    {
        public Task<TrustedHostKey?> GetAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) => Task.FromResult<TrustedHostKey?>(null);

        public Task TrustAsync(SshEndpoint endpoint, HostKeyIdentity identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> RemoveAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoRoutedTrust : IRoutedHostKeyTrustStore
    {
        public Task<TrustedRoutedHostKey?> GetAsync(SshRoute route, CancellationToken cancellationToken = default) => Task.FromResult<TrustedRoutedHostKey?>(null);

        public Task TrustAsync(SshRoute route, HostKeyIdentity identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> RemoveAsync(SshRoute route, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>The real server/profile/trust services over a fresh folder under the test QA root.</summary>
    private sealed class SeedWorld : IDisposable
    {
        public SeedWorld()
        {
            Root = Path.Combine(QaTestRoots.Root, "editor-seed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var gate = new ConfigurationWriteGate();
            Repository = new JsonServerRepository(new ServerStorageOptions { FilePath = Path.Combine(Root, "servers.json") }, NullLogger<JsonServerRepository>.Instance, gate);
            Servers = new ServerService(Repository, new ServerValidator(), gate);
            Profiles = new ServerProfileService(Servers, Credentials, gate);
            Trust = new JsonHostKeyTrustStore(new HostKeyTrustStorageOptions { FilePath = Path.Combine(Root, "known-hosts.json") }, NullLogger<JsonHostKeyTrustStore>.Instance, gate);
        }

        public string Root { get; }

        public JsonServerRepository Repository { get; }

        public ServerService Servers { get; }

        public ServerProfileService Profiles { get; }

        public JsonHostKeyTrustStore Trust { get; }

        public ViewModels.RecordingCredentialStore Credentials { get; } = new();

        public void Dispose()
        {
            Trust.Dispose();
            Servers.Dispose();
            Repository.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
