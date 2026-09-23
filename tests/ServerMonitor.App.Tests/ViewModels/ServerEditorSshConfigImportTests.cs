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
    [InlineData("ProxyJump bastion")]
    [InlineData("ProxyCommand nc bastion 22")]
    public void Apply_RefusesProxyJumpHost(string proxyLine)
    {
        var vm = Editor();

        Assert.False(vm.ApplySshConfigHost(Entry($"Host inner\n  HostName 10.1.0.9\n  User u\n  {proxyLine}\n", "inner")));

        Assert.Equal(string.Empty, vm.Name);
        Assert.Equal(string.Empty, vm.Host);
        Assert.Equal(string.Empty, vm.Username);
        Assert.Equal("22", vm.Port);
    }

    [Fact]
    public async Task Load_ListsHostsWithPreviewAndClassification_AndChangesNothing()
    {
        var result = SshConfigResolver.Import(
            FullConfig + "\nHost inner\n  ProxyJump web\n  ServerAliveInterval 5\nInclude extra\n",
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
    public async Task Load_ClassifiedErrors_AreShownNotThrown(SshConfigImportErrorCode code)
    {
        var vm = Editor(result: SshConfigImportResult.Failed(code));

        await vm.LoadSshConfigHostsAsync();

        Assert.False(vm.HasSshConfigHosts);
        Assert.Equal($"SshConfigImportError{code}", vm.SshConfigStatusMessage);
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
