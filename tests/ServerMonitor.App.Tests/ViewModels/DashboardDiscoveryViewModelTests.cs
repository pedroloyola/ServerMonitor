using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;

namespace ServerMonitor.App.Tests.ViewModels;

public sealed class DashboardDiscoveryViewModelTests
{
    [Theory]
    [InlineData("EXAMPLE.LOCAL.", 22)]
    [InlineData("example.local", 22)]
    public void ConfiguredExactHostnameEndpoint_SuppressesDiscovery(string configuredHost, int port)
    {
        var server = Server(configuredHost, port);
        var discovered = DiscoveredServerViewModelTests.Service(
            "Example SSH", "example.local", 22, ["10.0.0.8"]);

        Assert.True(IsAlreadyConfigured([server], discovered));
    }

    [Fact]
    public void ConfiguredExactAddressEndpoint_SuppressesDiscovery()
    {
        var discovered = DiscoveredServerViewModelTests.Service(
            "Example SSH", "example.local", 22, ["10.0.0.8"]);

        Assert.True(IsAlreadyConfigured([Server("10.0.0.8", 22)], discovered));
    }

    [Fact]
    public void BracketedScopedIpv6ConfiguredEndpoint_SuppressesDiscovery()
    {
        var discovered = DiscoveredServerViewModelTests.Service(
            "IPv6 SSH", "server.local", 22, ["fe80::42%7"]);

        Assert.True(IsAlreadyConfigured([Server("[fe80::42%7]", 22)], discovered));
    }

    [Theory]
    [InlineData("example.local", 2222)]
    [InlineData("other.local", 22)]
    [InlineData("10.0.0.9", 22)]
    public void DistinctEndpoint_RemainsVisible(string configuredHost, int configuredPort)
    {
        var discovered = DiscoveredServerViewModelTests.Service(
            "Example SSH", "example.local", 22, ["10.0.0.8"]);

        Assert.False(IsAlreadyConfigured([Server(configuredHost, configuredPort)], discovered));
    }

    [Fact]
    public void HiddenConfiguredServer_StillSuppressesExactDiscoveryEndpoint()
    {
        var configured = Server("example.local", 22) with { IsHidden = true };
        var discovered = DiscoveredServerViewModelTests.Service("Example SSH", "example.local", 22);

        Assert.True(IsAlreadyConfigured([configured], discovered));
    }

    [Fact]
    public async Task DiscoveryAdd_CancelDoesNotCallProfileAdd()
    {
        var profile = new RecordingProfileService();
        var dialog = new FakeDialogService { DiscoveryResult = null };
        var discovered = DiscoveredServerViewModelTests.Service("Example SSH", "example.local", 22);
        var vm = CreateUninitializedDashboard(profile, dialog);
        var row = DiscoveryRow(discovered);

        await InvokeAddDiscoveredAsync(vm, row);

        Assert.Equal(1, dialog.DiscoveryShowCount);
        Assert.Equal(0, profile.AddCount);
        Assert.Equal("Example SSH", dialog.LastPrefill!.Name);
        Assert.Equal("example.local", dialog.LastPrefill.Host);
        Assert.Equal(22, dialog.LastPrefill.Port);
    }

    [Fact]
    public async Task DiscoverySave_UsesNormalServerProfileServicePath()
    {
        var repository = new InMemoryRepository();
        using var serverService = new ServerService(repository, new ServerValidator());
        var credentials = new RecordingCredentialStore();
        var profiles = new ServerProfileService(serverService, credentials);
        var dialog = new FakeDialogService
        {
            DiscoveryResult = EditorResult("Example SSH", "example.local", 22)
        };
        var discovered = DiscoveredServerViewModelTests.Service("Example SSH", "example.local", 22);
        var vm = CreateUninitializedDashboard(profiles, dialog);

        await InvokeAddDiscoveredAsync(vm, DiscoveryRow(discovered));

        var saved = Assert.Single(await repository.GetAllAsync());
        Assert.Equal("Example SSH", saved.Name);
        Assert.Equal("example.local", saved.Host);
        Assert.Equal(22, saved.Port);
        Assert.Equal(ServerOperatingSystem.Auto, saved.OperatingSystem);
        Assert.Equal(AuthenticationMethod.SshKey, saved.AuthenticationMethod);
        Assert.Equal(0, credentials.ReadCount);
        Assert.Equal(0, credentials.WriteCount);
        Assert.Equal(0, credentials.DeleteCount);
    }

    private static DashboardViewModel CreateUninitializedDashboard(
        IServerProfileService profileService,
        IServerDialogService dialog)
    {
        // Dashboard captures a WinUI DispatcherQueue in its constructor. These contract tests run
        // without a WinUI runtime, so invoke the private add/suppression units on an uninitialized
        // instance with only their exact collaborators populated.
        var vm = (DashboardViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DashboardViewModel));
        SetField(vm, "_serverProfileService", profileService);
        SetField(vm, "_dialogService", dialog);
        SetField(vm, "_connectionStateStore", new FakeConnectionStateStore());
        SetField(vm, "_logger", NullLogger<DashboardViewModel>.Instance);
        return vm;
    }

    private static bool IsAlreadyConfigured(IReadOnlyCollection<Server> servers, DiscoveredService discovered)
    {
        var vm = (DashboardViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DashboardViewModel));
        var build = typeof(DashboardViewModel).GetMethod(
            "BuildConfiguredEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!;
        SetField(vm, "_configuredEndpoints", build.Invoke(null, [servers])!);
        var method = typeof(DashboardViewModel).GetMethod(
            "IsAlreadyConfigured", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(vm, [discovered])!;
    }

    private static DiscoveredServerViewModel DiscoveryRow(DiscoveredService discovered) => new(
        discovered,
        new FakeLocalizationService(),
        _ => Task.CompletedTask,
        _ => Task.CompletedTask);

    private static async Task InvokeAddDiscoveredAsync(
        DashboardViewModel vm,
        DiscoveredServerViewModel discovered)
    {
        var method = typeof(DashboardViewModel).GetMethod(
            "AddDiscoveredAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(vm, [discovered])!;
    }

    private static void SetField(object instance, string name, object value) =>
        typeof(DashboardViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private static ServerEditorResult EditorResult(string name, string host, int port) => new()
    {
        Profile = new ServerProfileInput
        {
            Configuration = new ServerInput
            {
                Name = name,
                Host = host,
                Port = port,
                Username = "monitor",
                OperatingSystem = ServerOperatingSystem.Auto,
                AuthenticationMethod = AuthenticationMethod.SshKey,
                PrivateKeyPath = Path.Combine(Path.GetTempPath(), "id_discovery_test")
            },
            CredentialChange = CredentialChange.Clear
        }
    };

    internal static Server ServerForTest(string host, int port) => new()
    {
        Id = Guid.NewGuid(),
        Name = "configured",
        Host = host,
        Port = port,
        Username = "monitor",
        OperatingSystem = ServerOperatingSystem.Auto,
        AuthenticationMethod = AuthenticationMethod.SshKey,
        PrivateKeyPath = Path.Combine(Path.GetTempPath(), "id_test"),
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    };

    private static Server Server(string host, int port) => ServerForTest(host, port);

    private sealed class RecordingProfileService : IServerProfileService
    {
        public int AddCount { get; private set; }
        public Task<ServerOperationResult> AddAsync(ServerProfileInput input, CancellationToken cancellationToken = default)
        {
            AddCount++;
            return Task.FromResult(ServerOperationResult.Failure());
        }
        public Task<ServerOperationResult> UpdateAsync(Server existingServer, ServerProfileInput input,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RemoveAsync(Server server, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDialogService : IServerDialogService
    {
        public ServerEditorResult? DiscoveryResult { get; init; }
        public int DiscoveryShowCount { get; private set; }
        public ServerDiscoveryPrefill? LastPrefill { get; private set; }
        public Task<ServerEditorResult?> ShowEditorForDiscoveryAsync(ServerDiscoveryPrefill prefill)
        {
            DiscoveryShowCount++;
            LastPrefill = prefill;
            return Task.FromResult(DiscoveryResult);
        }
        public Task<ServerEditorResult?> ShowEditorAsync(Server? server) => Task.FromResult<ServerEditorResult?>(null);
        public Task<bool> ConfirmRemoveAsync(Server server) => Task.FromResult(false);
    }

    private sealed class InMemoryRepository : IServerRepository
    {
        private IReadOnlyList<Server> _servers = [];
        public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_servers);
        public Task SaveAllAsync(IReadOnlyCollection<Server> servers, CancellationToken cancellationToken = default)
        {
            _servers = servers.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCredentialStore : IServerCredentialStore
    {
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public int DeleteCount { get; private set; }
        public Task WriteAsync(CredentialReference reference, SecretValue secret,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return Task.CompletedTask;
        }
        public Task<SecretValue?> ReadAsync(CredentialReference reference,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult<SecretValue?>(null);
        }
        public Task<bool> DeleteAsync(CredentialReference reference,
            CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            return Task.FromResult(true);
        }
    }
}
