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
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-save=maybe")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-seed=direct", "--qa-editor-seed=routed")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-start=editor-edit:1")] // nothing seeded
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-editor-seed=direct", "--qa-start=editor-edit:2")]
    [InlineData("--qa-editor", "--qa-backup", "ok", "--qa-start=overview")]
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
        // The seed is the harness's hosted service (resolved through its own factory; the production hosted services
        // registered by the root are not constructed here).
        var seed = provider.GetRequiredService<QaEditorSeed>();
        var hosted = app.Services.Last(descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.Same(seed, hosted.ImplementationFactory!(provider));
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
        await new QaEditorSeed(seed, world.Profiles, world.Root).StartAsync(CancellationToken.None);

        var server = Assert.Single(await world.Servers.GetAllAsync());
        Assert.Equal(routed, server.Route is not null);
        Assert.Equal(seed == "password" || routed ? 1 : 0, world.Credentials.Count);
    }

    [Fact]
    public void TheCatalogue_IsClosed_AndEveryOutcomeAnswersForTheMatchingForm()
    {
        Assert.Equal(23, QaScriptedSshConnectionService.Outcomes.Count);
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

    private static IsolatedAppComposition Compose(IReadOnlyList<string> args)
    {
        var app = new IsolatedAppComposition();
        QaStartupIsolation.Apply(app.Services, Path.Combine(QaTestRoots.Root, "isolated", "editor-" + Guid.NewGuid().ToString("N")), rerootSshProfile: true);
        QaEditorComposition.Apply(app.Services, args);
        return app;
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
