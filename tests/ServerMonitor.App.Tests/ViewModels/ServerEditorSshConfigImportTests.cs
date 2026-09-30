using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.App.Tests.ViewModels;

public sealed class ServerEditorSshConfigImportTests
{
    private const string Profile = @"C:\Users\tester";

    private sealed class FakeSshConfigSource(SshConfigImportResult result) : ISshConfigImportSource
    {
        public int LoadCount { get; private set; }

        public Task<SshConfigImportResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingSshConnectionService : ISshConnectionService
    {
        public Task<SshConnectionResult> ConnectAsync(SshConnectionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("import must not connect");

        public Task<SshConnectionResult> TestConnectionAsync(SshConnectionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("import must not connect");

        public Task<SshConnectionResult> DetectOperatingSystemAsync(SshConnectionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("import must not connect");
    }

    private sealed class ThrowingHostKeyTrustStore : IHostKeyTrustStore
    {
        public Task<TrustedHostKey?> GetAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("import must not touch trust");

        public Task TrustAsync(SshEndpoint endpoint, HostKeyIdentity identity, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("import must not touch trust");

        public Task<bool> RemoveAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("import must not touch trust");
    }

    private sealed class NullPicker : IPrivateKeyFilePicker
    {
        public Task<string?> PickAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private static ServerEditorViewModel Editor(
        Server? server = null,
        SshConfigImportResult? result = null,
        ServerDiscoveryPrefill? prefill = null,
        FakeSshConfigSource? source = null) =>
        new(
            new ServerValidator(),
            new ThrowingSshConnectionService(),
            new ThrowingHostKeyTrustStore(),
            new FakeConnectionStateStore(),
            new NullPicker(),
            new FakeLocalizationService(),
            server,
            prefill,
            source ?? new FakeSshConfigSource(result ?? SshConfigImportResult.NotFound));

    private static SshConfigHostEntry Entry(string text, string alias) =>
        Assert.Single(SshConfigResolver.Import(text, Profile).Hosts, host => host.Alias == alias);

    private const string FullConfig =
        """
        Host web
            HostName 10.0.0.5
            User deploy
            Port 2222
            IdentityFile ~/.ssh/id_web
        """;

    [Fact]
    public void Apply_FillsEveryEmptyFieldOfANewServer()
    {
        var vm = Editor();

        Assert.True(vm.ApplySshConfigHost(Entry(FullConfig, "web")));

        Assert.Equal("web", vm.Name);
        Assert.Equal("10.0.0.5", vm.Host);
        Assert.Equal("2222", vm.Port);
        Assert.Equal("deploy", vm.Username);
        Assert.True(vm.IsPrivateKeyAuthentication);
        Assert.Equal(@"C:\Users\tester\.ssh\id_web", vm.PrivateKeyPath);
    }

    [Fact]
    public void Apply_NeverOverwritesTypedFields()
    {
        var vm = Editor();
        vm.Name = "My web";
        vm.Host = "typed.example.com";
        vm.Port = "2200";
        vm.Username = "me";

        Assert.True(vm.ApplySshConfigHost(Entry(FullConfig, "web")));

        Assert.Equal("My web", vm.Name);
        Assert.Equal("typed.example.com", vm.Host);
        Assert.Equal("2200", vm.Port);
        Assert.Equal("me", vm.Username);
        Assert.Equal(string.Empty, vm.PrivateKeyPath);
    }

    [Fact]
    public void Apply_TypedHostEqualToEntryHost_FillsOnlyTheEmptyRest()
    {
        var vm = Editor();
        vm.Host = "10.0.0.5";
        vm.Username = "me";

        vm.ApplySshConfigHost(Entry(FullConfig, "web"));

        Assert.Equal("web", vm.Name);
        Assert.Equal("10.0.0.5", vm.Host);
        Assert.Equal("me", vm.Username);
        Assert.Equal("2222", vm.Port);
        Assert.Equal(@"C:\Users\tester\.ssh\id_web", vm.PrivateKeyPath);
    }

    [Fact]
    public void Apply_TypedDifferentHost_NeverMixesUserPortOrKeyIntoIt()
    {
        var vm = Editor();
        vm.Host = "other.example.com";

        Assert.True(vm.ApplySshConfigHost(Entry(FullConfig, "web")));

        Assert.Equal("other.example.com", vm.Host);
        Assert.Equal(string.Empty, vm.Username);
        Assert.Equal("22", vm.Port);
        Assert.Equal(string.Empty, vm.PrivateKeyPath);
        Assert.Equal("web", vm.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%h.corp")]
    public void Apply_UnresolvedHostName_NeverFillsUserPortOrKey(string typedHost)
    {
        var vm = Editor();
        vm.Host = typedHost;

        Assert.True(vm.ApplySshConfigHost(Entry(
            "Host a\n  HostName %h.corp\n  User u\n  Port 2222\n  IdentityFile ~/.ssh/id_a\n",
            "a")));

        Assert.Equal(typedHost, vm.Host);
        Assert.Equal(string.Empty, vm.Username);
        Assert.Equal("22", vm.Port);
        Assert.Equal(string.Empty, vm.PrivateKeyPath);
        Assert.Equal("a", vm.Name);
    }

    [Fact]
    public void Apply_PortTypedAs22_IsNeverReplaced()
    {
        var vm = Editor();
        vm.Port = "2";
        vm.Port = "22";

        vm.ApplySshConfigHost(Entry(FullConfig, "web"));

        Assert.Equal("22", vm.Port);
        Assert.Equal("deploy", vm.Username);
    }

    [Fact]
    public void Apply_NeverOverwritesPasswordAuthenticationOrInfersIt()
    {
        var vm = Editor();
        vm.SelectedAuthenticationIndex = 1;

        vm.ApplySshConfigHost(Entry(FullConfig, "web"));

        Assert.True(vm.IsPasswordAuthentication);
        Assert.Equal(string.Empty, vm.PrivateKeyPath);

        var keyless = Editor();
        keyless.ApplySshConfigHost(Entry("Host a\n  User u\n", "a"));
        Assert.True(keyless.IsPrivateKeyAuthentication);
        Assert.Equal(string.Empty, keyless.PrivateKeyPath);
    }

    [Fact]
    public void Apply_NeverOverwritesAChosenPrivateKey()
    {
        var vm = Editor();
        vm.PrivateKeyPath = @"D:\keys\mine";

        vm.ApplySshConfigHost(Entry(FullConfig, "web"));

        Assert.Equal(@"D:\keys\mine", vm.PrivateKeyPath);
    }

    [Fact]
    public void Apply_AmbiguousIdentityFile_LeavesTheKeyForTheUserToChoose()
    {
        var vm = Editor();

        vm.ApplySshConfigHost(Entry("Host a\n  IdentityFile ~/.ssh/x\n  IdentityFile ~/.ssh/y\n", "a"));

        Assert.Equal(string.Empty, vm.PrivateKeyPath);
    }

    [Fact]
    public async Task EditMode_ImportIsUnavailable_AndSavedServerValuesAreNeverChanged()
    {
        var saved = new Server
        {
            Id = Guid.NewGuid(),
            Name = "Prod",
            Host = "prod.lan",
            Port = 22,
            Username = "root",
            AuthenticationMethod = AuthenticationMethod.Password,
            CredentialReferenceId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UnixEpoch
        };
        var snapshot = saved with { };
        var source = new FakeSshConfigSource(SshConfigResolver.Import(FullConfig, Profile));
        var vm = Editor(server: saved, source: source);

        Assert.False(vm.IsSshConfigImportAvailable);
        await vm.LoadSshConfigHostsAsync();
        Assert.False(vm.ApplySshConfigHost(Entry(FullConfig, "web")));

        Assert.Equal(0, source.LoadCount);
        Assert.False(vm.HasSshConfigHosts);

        Assert.Equal("Prod", vm.Name);
        Assert.Equal("prod.lan", vm.Host);
        Assert.Equal("22", vm.Port);
        Assert.Equal("root", vm.Username);
        Assert.True(vm.IsPasswordAuthentication);
        Assert.Equal(snapshot, saved);
    }

    [Fact]
    public void Apply_DiscoveredPortIsKeptLikeATypedValue()
    {
        var vm = Editor(prefill: new ServerDiscoveryPrefill { Name = "nas", Host = "10.0.0.5", Port = 22 });

        vm.ApplySshConfigHost(Entry(FullConfig, "web"));

        Assert.Equal("22", vm.Port);
        Assert.Equal("nas", vm.Name);
        Assert.Equal("deploy", vm.Username);
    }

    [Theory]
    [InlineData("ProxyJump bastion,outer")]
    [InlineData("ProxyJump ssh://bastion")]
    [InlineData("ProxyJump inner")]
    [InlineData("ProxyCommand nc bastion 22")]
    public void Apply_RefusesBlockedProxyHost(string proxyLine)
    {
        var vm = Editor();

        Assert.False(vm.ApplySshConfigHost(Entry($"Host inner\n  HostName 10.1.0.9\n  User u\n  {proxyLine}\n", "inner")));

        Assert.Equal(string.Empty, vm.Name);
        Assert.Equal(string.Empty, vm.Host);
        Assert.Equal(string.Empty, vm.Username);
        Assert.Equal("22", vm.Port);
        Assert.False(vm.UseJumpHost);
        Assert.Equal(string.Empty, vm.JumpHost);
    }

    [Fact]
    public async Task Load_ListsHostsWithPreviewAndClassification_AndChangesNothing()
    {
        var result = SshConfigResolver.Import(
            FullConfig + "\nHost inner\n  ProxyJump web,other\n  ServerAliveInterval 5\nInclude extra\n",
            Profile);
        var vm = Editor(result: result);

        await vm.LoadSshConfigHostsAsync();

        Assert.True(vm.IsSshConfigImportOpen);
        Assert.Equal(["web", "inner"], vm.SshConfigHosts.Select(h => h.Alias));
        var inner = vm.SshConfigHosts[1];
        Assert.False(inner.IsImportable);
        Assert.True(inner.HasRequirement);
        Assert.True(inner.HasClassification);
        Assert.True(vm.SshConfigHosts[0].IsImportable);
        Assert.True(vm.HasSshConfigFileWarning);
        Assert.Equal(string.Empty, vm.Name);
        Assert.Equal(string.Empty, vm.Host);
    }

    [Fact]
    public async Task Load_MissingConfig_ShowsEmptyNonErrorState()
    {
        var vm = Editor(result: SshConfigImportResult.NotFound);

        await vm.LoadSshConfigHostsAsync();

        Assert.False(vm.HasSshConfigHosts);
        Assert.Equal("SshConfigImportNotFound", vm.SshConfigStatusMessage);
    }

    [Theory]
    [InlineData(SshConfigImportErrorCode.TooLarge)]
    [InlineData(SshConfigImportErrorCode.Unreadable)]
    [InlineData(SshConfigImportErrorCode.InvalidEncoding)]
    [InlineData(SshConfigImportErrorCode.ConfigIsLink)]
    public async Task Load_ClassifiedErrors_AreShownNotThrown(SshConfigImportErrorCode code)
    {
        var vm = Editor(result: SshConfigImportResult.Failed(code));

        await vm.LoadSshConfigHostsAsync();

        Assert.False(vm.HasSshConfigHosts);
        Assert.Equal($"SshConfigImportError{code}", vm.SshConfigStatusMessage);
    }

    [Fact]
    public async Task Load_IncludeDiagnostics_AreListedWithTheArgumentAsWritten()
    {
        var vm = Editor(result: new SshConfigImportResult
        {
            Status = SshConfigImportStatus.Loaded,
            Hosts = [Entry("Host a\n", "a")],
            Diagnostics =
            [
                new SshConfigDiagnostic(SshConfigDiagnosticKind.IncludeMatchedNoFiles, "conf.d/*.conf"),
                new SshConfigDiagnostic(
                    SshConfigDiagnosticKind.IncludeNotVerified,
                    "../x",
                    SshConfigIncludeIssue.OutsideSshDirectory)
            ]
        });

        await vm.LoadSshConfigHostsAsync();

        Assert.Equal(
            "Include 'conf.d/*.conf' matched no files." + Environment.NewLine
                + "Include '../x' was not followed (SshConfigIncludeIssueOutsideSshDirectory).",
            vm.SshConfigFileWarningMessage);
    }

    [Theory]
    [InlineData(SshConfigImportErrorCode.IncludeCycle)]
    [InlineData(SshConfigImportErrorCode.IncludeTooDeep)]
    [InlineData(SshConfigImportErrorCode.TooManyFiles)]
    public async Task Load_IncludeErrors_NameTheFile(SshConfigImportErrorCode code)
    {
        var vm = Editor(result: SshConfigImportResult.Failed(code) with { ErrorDetail = @"C:\Users\tester\.ssh\a" });

        await vm.LoadSshConfigHostsAsync();

        Assert.False(vm.HasSshConfigHosts);
        Assert.Equal($@"SshConfigImportError{code} File: C:\Users\tester\.ssh\a", vm.SshConfigStatusMessage);
    }

    private sealed class BlockingSshConfigSource : ISshConfigImportSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken SeenToken { get; private set; }

        public async Task<SshConfigImportResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            SeenToken = cancellationToken;
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return SshConfigImportResult.Failed(SshConfigImportErrorCode.Unreadable);
        }
    }

    [Fact]
    public async Task Close_CancelsAnInFlightLoad_WithoutAnyVisibleError()
    {
        var source = new BlockingSshConfigSource();
        var vm = new ServerEditorViewModel(
            new ServerValidator(),
            new ThrowingSshConnectionService(),
            new ThrowingHostKeyTrustStore(),
            new FakeConnectionStateStore(),
            new NullPicker(),
            new FakeLocalizationService(),
            server: null,
            prefill: null,
            sshConfigImportSource: source);

        var load = vm.LoadSshConfigHostsAsync();
        await source.Started.Task;
        Assert.True(vm.IsLoadingSshConfig);

        vm.CloseSshConfigImport();
        await load;

        Assert.True(source.SeenToken.IsCancellationRequested);
        Assert.False(vm.IsLoadingSshConfig);
        Assert.False(vm.IsSshConfigImportOpen);
        Assert.False(vm.HasSshConfigHosts);
        Assert.False(vm.HasSshConfigStatus);
        Assert.False(vm.HasSshConfigFileWarning);
    }

    [Fact]
    public async Task Dispose_CancelsAnInFlightLoad()
    {
        var source = new BlockingSshConfigSource();
        var vm = new ServerEditorViewModel(
            new ServerValidator(),
            new ThrowingSshConnectionService(),
            new ThrowingHostKeyTrustStore(),
            new FakeConnectionStateStore(),
            new NullPicker(),
            new FakeLocalizationService(),
            server: null,
            prefill: null,
            sshConfigImportSource: source);

        var load = vm.LoadSshConfigHostsAsync();
        await source.Started.Task;
        vm.Dispose();
        await load;

        Assert.True(source.SeenToken.IsCancellationRequested);
        Assert.False(vm.IsLoadingSshConfig);
        Assert.False(vm.HasSshConfigStatus);
    }

    // ---- M14.4c: single-hop ProxyJump import

    private const string JumpConfig =
        """
        Host inner
            HostName 10.1.0.9
            User app
            Port 2201
            IdentityFile ~/.ssh/inner_key
            ProxyJump bastion
        Host bastion
            HostName 203.0.113.7
            User jumper
            Port 2222
            IdentityFile ~/.ssh/jump_key
        """;

    [Fact]
    public void Apply_JumpHost_FillsTheRouteAndEveryEmptyJumpField()
    {
        var vm = Editor();

        Assert.True(vm.ApplySshConfigHost(Entry(JumpConfig, "inner")));

        Assert.Equal("inner", vm.Name);
        Assert.Equal("10.1.0.9", vm.Host);
        Assert.Equal("2201", vm.Port);
        Assert.Equal("app", vm.Username);
        Assert.True(vm.UseJumpHost);
        Assert.Equal("203.0.113.7", vm.JumpHost);
        Assert.Equal("2222", vm.JumpPort);
        Assert.Equal("jumper", vm.JumpUsername);
        Assert.Equal(@"C:\Users\tester\.ssh\jump_key", vm.JumpPrivateKeyPath);
        Assert.True(vm.IsJumpPrivateKeyAuthentication);
        Assert.False(vm.HasSavedJumpSecret);
    }

    [Fact]
    public void Apply_JumpHost_SavesAsARoutedServer_NeverDirect_AndSetsNoSecret()
    {
        var vm = Editor();
        Assert.True(vm.ApplySshConfigHost(Entry(JumpConfig, "inner")));

        Assert.True(vm.TryCreateResult(out var result));

        var configuration = result!.Profile.Configuration;
        Assert.Equal("10.1.0.9", configuration.Host);
        var jump = Assert.IsType<JumpHop>(configuration.Route?.Jump);
        Assert.Equal("203.0.113.7", jump.Host);
        Assert.Equal(2222, jump.Port);
        Assert.Equal("jumper", jump.Username);
        Assert.Equal(AuthenticationMethod.SshKey, jump.AuthenticationMethod);
        Assert.Equal(@"C:\Users\tester\.ssh\jump_key", jump.PrivateKeyPath);
        Assert.Null(jump.CredentialReferenceId);
        Assert.Null(result.Profile.JumpCredentialChange);
        Assert.Null(result.Profile.CredentialChange.Secret);
    }

    [Fact]
    public void Apply_JumpWithoutUserOrPort_LeavesTheUserEmptyAndTheDefaultPort()
    {
        var vm = Editor();

        Assert.True(vm.ApplySshConfigHost(Entry("Host inner\n  HostName 10.1.0.9\n  ProxyJump bastion\n", "inner")));

        Assert.True(vm.UseJumpHost);
        Assert.Equal("bastion", vm.JumpHost);
        Assert.Equal("22", vm.JumpPort);
        Assert.Equal(string.Empty, vm.JumpUsername);
        Assert.Equal(string.Empty, vm.JumpPrivateKeyPath);
    }

    [Fact]
    public void Apply_ExplicitJumpUserAndPort_AreWhatTheFormGets()
    {
        var vm = Editor();

        Assert.True(vm.ApplySshConfigHost(Entry(JumpConfig.Replace("ProxyJump bastion", "ProxyJump ops@bastion:2200"), "inner")));

        Assert.Equal("ops", vm.JumpUsername);
        Assert.Equal("2200", vm.JumpPort);
    }

    [Fact]
    public void Apply_NeverOverwritesTypedJumpFields()
    {
        var vm = Editor();
        vm.JumpHost = "203.0.113.7";
        vm.JumpPort = "2";
        vm.JumpPort = "22";
        vm.JumpUsername = "mine";
        vm.JumpPrivateKeyPath = @"C:\keys\mine";

        Assert.True(vm.ApplySshConfigHost(Entry(JumpConfig, "inner")));

        Assert.True(vm.UseJumpHost);
        Assert.Equal("203.0.113.7", vm.JumpHost);
        Assert.Equal("22", vm.JumpPort);
        Assert.Equal("mine", vm.JumpUsername);
        Assert.Equal(@"C:\keys\mine", vm.JumpPrivateKeyPath);
    }

    [Fact]
    public void Apply_TypedDifferentJumpHost_IsKept_AndNeverGetsTheImportedJumpsUserPortOrKey()
    {
        var vm = Editor();
        vm.UseJumpHost = true;
        vm.JumpHost = "other-bastion";

        Assert.True(vm.ApplySshConfigHost(Entry(JumpConfig, "inner")));

        Assert.True(vm.UseJumpHost);
        Assert.Equal("other-bastion", vm.JumpHost);
        Assert.Equal("22", vm.JumpPort);
        Assert.Equal(string.Empty, vm.JumpUsername);
        Assert.Equal(string.Empty, vm.JumpPrivateKeyPath);
    }

    [Fact]
    public void Apply_JumpPasswordAuthentication_IsNeverChangedOrGivenAKey()
    {
        var vm = Editor();
        vm.SelectedJumpAuthenticationIndex = 1;

        Assert.True(vm.ApplySshConfigHost(Entry(JumpConfig, "inner")));

        Assert.Equal(1, vm.SelectedJumpAuthenticationIndex);
        Assert.Equal(string.Empty, vm.JumpPrivateKeyPath);
    }

    [Fact]
    public void Apply_JumpHostWithUnresolvedTargetHostName_StillRoutesThroughTheJump()
    {
        var vm = Editor();

        Assert.True(vm.ApplySshConfigHost(Entry("Host inner\n  HostName %h.corp\n  ProxyJump bastion\n", "inner")));

        Assert.Equal(string.Empty, vm.Host);
        Assert.True(vm.UseJumpHost);
        Assert.Equal("bastion", vm.JumpHost);
    }

    [Fact]
    public void Apply_JumpHostIntoADifferentTypedHost_FillsOnlyTheName_AndLeavesTheRouteAlone()
    {
        var vm = Editor();
        vm.Host = "192.0.2.50";

        Assert.True(vm.ApplySshConfigHost(Entry(JumpConfig, "inner")));

        Assert.Equal("inner", vm.Name);
        Assert.Equal("192.0.2.50", vm.Host);
        Assert.False(vm.UseJumpHost);
        Assert.Equal(string.Empty, vm.JumpHost);
    }

    [Fact]
    public void Apply_DirectHost_NeverTouchesTheRoute()
    {
        var vm = Editor();

        Assert.True(vm.ApplySshConfigHost(Entry(FullConfig, "web")));

        Assert.False(vm.UseJumpHost);
        Assert.Equal(string.Empty, vm.JumpHost);
        Assert.Equal("22", vm.JumpPort);
    }

    [Fact]
    public async Task Load_JumpHostIsImportable_AndItsRouteIsPreviewed()
    {
        var vm = Editor(result: SshConfigResolver.Import(JumpConfig, Profile));

        await vm.LoadSshConfigHostsAsync();

        var inner = Assert.Single(vm.SshConfigHosts, h => h.Alias == "inner");
        Assert.True(inner.IsImportable);
        Assert.False(inner.HasRequirement);
        Assert.Contains("Via: bastion", inner.Preview);
        Assert.False(vm.UseJumpHost);
        Assert.Equal(string.Empty, vm.JumpHost);
    }

    [Fact]
    public void NoImportSource_ImportIsUnavailable()
    {
        var vm = new ServerEditorViewModel(
            new ServerValidator(),
            new ThrowingSshConnectionService(),
            new ThrowingHostKeyTrustStore(),
            new FakeConnectionStateStore(),
            new NullPicker(),
            new FakeLocalizationService(),
            null);

        Assert.False(vm.IsSshConfigImportAvailable);
    }
}
