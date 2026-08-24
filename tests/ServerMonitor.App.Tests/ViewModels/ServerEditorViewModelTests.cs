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

        public Task<SshConnectionResult> ConnectAsync(
            SshConnectionRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(Result);

        public Task<SshConnectionResult> TestConnectionAsync(
            SshConnectionRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(Result);

        public Task<SshConnectionResult> DetectOperatingSystemAsync(
            SshConnectionRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }

    private sealed class FakeHostKeyTrustStore : IHostKeyTrustStore
    {
        public Task<TrustedHostKey?> GetAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) =>
            Task.FromResult<TrustedHostKey?>(null);

        public Task TrustAsync(SshEndpoint endpoint, HostKeyIdentity identity, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> RemoveAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakePrivateKeyFilePicker : IPrivateKeyFilePicker
    {
        public string? PickedPath { get; set; }
        public Task<string?> PickAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PickedPath);
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
