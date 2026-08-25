using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

public sealed class ServerEditorViewModelTests
{
    private sealed class FakeSshConnectionService : ISshConnectionService
    {
        public SshConnectionResult Result { get; set; } = TestData.Connected();

        public int ConnectCount { get; private set; }

        public int TestConnectionCount { get; private set; }

        public int DetectOperatingSystemCount { get; private set; }

        public Task<SshConnectionResult> ConnectAsync(
            SshConnectionRequest request,
            CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            return Task.FromResult(Result);
        }

        public Task<SshConnectionResult> TestConnectionAsync(
            SshConnectionRequest request,
            CancellationToken cancellationToken = default)
        {
            TestConnectionCount++;
            return Task.FromResult(Result);
        }

        public Task<SshConnectionResult> DetectOperatingSystemAsync(
            SshConnectionRequest request,
            CancellationToken cancellationToken = default)
        {
            DetectOperatingSystemCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeHostKeyTrustStore : IHostKeyTrustStore
    {
        public int GetCount { get; private set; }

        public int TrustCount { get; private set; }

        public int RemoveCount { get; private set; }

        public Task<TrustedHostKey?> GetAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) =>
            CountGet();

        public Task TrustAsync(SshEndpoint endpoint, HostKeyIdentity identity, CancellationToken cancellationToken = default)
        {
            TrustCount++;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default)
        {
            RemoveCount++;
            return Task.FromResult(true);
        }

        private Task<TrustedHostKey?> CountGet()
        {
            GetCount++;
            return Task.FromResult<TrustedHostKey?>(null);
        }
    }

    private sealed class FakePrivateKeyFilePicker : IPrivateKeyFilePicker
    {
        public string? PickedPath { get; set; }

        public int PickCount { get; private set; }

        public Task<string?> PickAsync(CancellationToken cancellationToken = default)
        {
            PickCount++;
            return Task.FromResult(PickedPath);
        }
    }

    [Fact]
    public void DiscoveryPrefill_SeedsOnlyNameHostPortAndPerformsNoPrivilegedAction()
    {
        var ssh = new FakeSshConnectionService();
        var trust = new FakeHostKeyTrustStore();
        var picker = new FakePrivateKeyFilePicker();
        var vm = new ServerEditorViewModel(
            new ServerValidator(),
            ssh,
            trust,
            new FakeConnectionStateStore(),
            picker,
            new FakeLocalizationService(),
            server: null,
            prefill: new ServerDiscoveryPrefill
            {
                Name = "Example SSH",
                Host = "example.local",
                Port = 22
            });

        Assert.Equal("Example SSH", vm.Name);
        Assert.Equal("example.local", vm.Host);
        Assert.Equal("22", vm.Port);
        Assert.Equal(string.Empty, vm.Username);
        Assert.Equal(string.Empty, vm.PrivateKeyPath);
        Assert.Equal((int)ServerOperatingSystem.Auto, vm.SelectedOperatingSystemIndex);
        Assert.Equal(0, ssh.ConnectCount);
        Assert.Equal(0, ssh.TestConnectionCount);
        Assert.Equal(0, ssh.DetectOperatingSystemCount);
        Assert.Equal(0, trust.GetCount);
        Assert.Equal(0, trust.TrustCount);
        Assert.Equal(0, trust.RemoveCount);
        Assert.Equal(0, picker.PickCount);
    }

    [Fact]
    public void NewServer_InitializesWithDefaults()
    {
        var vm = new ServerEditorViewModel(
            new ServerValidator(),
            new FakeSshConnectionService(),
            new FakeHostKeyTrustStore(),
            new FakeConnectionStateStore(),
            new FakePrivateKeyFilePicker(),
            new FakeLocalizationService(),
            null);

        Assert.Equal(string.Empty, vm.Name);
        Assert.Equal(string.Empty, vm.Host);
        Assert.Equal("22", vm.Port);
        Assert.Equal(string.Empty, vm.Username);
        Assert.Equal(0, vm.SelectedOperatingSystemIndex);
        Assert.Equal(0, vm.SelectedAuthenticationIndex);
        Assert.False(vm.HasValidationErrors);
    }

    [Fact]
    public void ExistingServer_LoadsValuesCorrectly()
    {
        var server = TestData.LinuxServer();
        var vm = new ServerEditorViewModel(
            new ServerValidator(),
            new FakeSshConnectionService(),
            new FakeHostKeyTrustStore(),
            new FakeConnectionStateStore(),
            new FakePrivateKeyFilePicker(),
            new FakeLocalizationService(),
            server);

        Assert.Equal(server.Name, vm.Name);
        Assert.Equal(server.Host, vm.Host);
        Assert.Equal(server.Port.ToString(), vm.Port);
        Assert.Equal(server.Username, vm.Username);
        Assert.Equal(1, vm.SelectedOperatingSystemIndex); // Linux
        Assert.Equal(1, vm.SelectedAuthenticationIndex); // Password
    }

    [Fact]
    public void TryCreateResult_ValidData_CreatesProfileResult()
    {
        var vm = new ServerEditorViewModel(
            new ServerValidator(),
            new FakeSshConnectionService(),
            new FakeHostKeyTrustStore(),
            new FakeConnectionStateStore(),
            new FakePrivateKeyFilePicker(),
            new FakeLocalizationService(),
            null)
        {
            Name = "Production Server",
            Host = "prod.example.com",
            Port = "2222",
            Username = "admin",
            PrivateKeyPath = "/path/to/id_ed25519"
        };

        var ok = vm.TryCreateResult(out var result);

        Assert.True(ok);
        Assert.NotNull(result);
        Assert.Equal("Production Server", result.Profile.Configuration.Name);
        Assert.Equal("prod.example.com", result.Profile.Configuration.Host);
        Assert.Equal(2222, result.Profile.Configuration.Port);
        Assert.Equal("admin", result.Profile.Configuration.Username);
    }

    [Fact]
    public void NewServer_DefaultsToThirtySecondInterval()
    {
        var vm = new ServerEditorViewModel(
            new ServerValidator(),
            new FakeSshConnectionService(),
            new FakeHostKeyTrustStore(),
            new FakeConnectionStateStore(),
            new FakePrivateKeyFilePicker(),
            new FakeLocalizationService(),
            null);

        // Index 1 == 30 s in RefreshIntervalPolicy.SupportedSeconds ([10, 30, 60, 300]).
        Assert.Equal(1, vm.SelectedRefreshIntervalIndex);
    }

    [Fact]
    public void ExistingServer_LoadsRefreshIntervalIndex()
    {
        var server = TestData.LinuxServer() with { RefreshIntervalSeconds = 60 };
        var vm = new ServerEditorViewModel(
            new ServerValidator(),
            new FakeSshConnectionService(),
            new FakeHostKeyTrustStore(),
            new FakeConnectionStateStore(),
            new FakePrivateKeyFilePicker(),
            new FakeLocalizationService(),
            server);

        Assert.Equal(2, vm.SelectedRefreshIntervalIndex); // 60 s -> index 2
    }

    [Fact]
    public void TryCreateResult_PersistsSelectedRefreshInterval()
    {
        var vm = new ServerEditorViewModel(
            new ServerValidator(),
            new FakeSshConnectionService(),
            new FakeHostKeyTrustStore(),
            new FakeConnectionStateStore(),
            new FakePrivateKeyFilePicker(),
            new FakeLocalizationService(),
            null)
        {
            Name = "Prod",
            Host = "prod.example.com",
            Port = "22",
            Username = "admin",
            PrivateKeyPath = "/path/to/id_ed25519",
            SelectedRefreshIntervalIndex = 3 // 300 s
        };

        var ok = vm.TryCreateResult(out var result);

        Assert.True(ok);
        Assert.Equal(300, result!.Profile.Configuration.RefreshIntervalSeconds);
    }

    [Fact]
    public void TryCreateResult_InvalidData_ReturnsFalseAndSetsError()
    {
        var vm = new ServerEditorViewModel(
            new ServerValidator(),
            new FakeSshConnectionService(),
            new FakeHostKeyTrustStore(),
            new FakeConnectionStateStore(),
            new FakePrivateKeyFilePicker(),
            new FakeLocalizationService(),
            null)
        {
            Name = "", // Invalid
            Host = "",
            Port = "0",
            Username = ""
        };

        var ok = vm.TryCreateResult(out var result);

        Assert.False(ok);
        Assert.Null(result);
        Assert.True(vm.HasValidationErrors);
    }
}
