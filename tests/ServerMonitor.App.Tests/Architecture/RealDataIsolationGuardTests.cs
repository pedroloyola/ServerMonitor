using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.TestSupport;
using ServerMonitor.App.Windowing;
using ServerMonitor.Core.Backup;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// TEST-REALDATA-AUDIT (UI.3 gate 1C). The isolated composition really is isolated, and the guard really sees a
/// violation. Every negative case points at a SENTINEL temp root that stands in for "real" - never at the real
/// %LOCALAPPDATA%, ~/.ssh or Credential Manager (the UI.2 S7 lesson) - and none of them constructs a store.
/// </summary>
public sealed class RealDataIsolationGuardTests : IDisposable
{
    private readonly IsolatedAppComposition _composition = new();

    private readonly string _sentinel = Path.Combine(Path.GetTempPath(), "sm-sentinel-real-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => _composition.Dispose();

    [Fact]
    public async Task The_isolated_real_composition_passes_both_phases()
    {
        await using var provider = _composition.BuildProvider();

        RealDataIsolationGuard.AssertIsolated(_composition.Services, provider, _composition.TestRoots);
    }

    [Fact]
    public void The_constructed_phase_builds_every_store_that_touches_a_per_user_file()
    {
        // Anti-vacuity for phase 2: these are the stores whose files the audit found (two of them READ on construction).
        string[] expected =
        [
            "IServerRepository", "IHostKeyTrustStore", "IRoutedHostKeyTrustStore", "IServerCredentialStore",
            "IConfigurationBackupService", "IServerHistoryStore", "IWidgetStateWriter", "IIgnoredDeviceStore",
            "JsonNotificationSettingsService", "JsonBackgroundMonitoringSettingsService", "JsonWindowPlacementStore",
            "ISshConfigImportSource", "ILocalSshKeyDiscovery"
        ];

        var built = RealDataIsolationGuard.DataPlaneDescriptors(_composition.Services).Select(d => d.ServiceType.Name).ToHashSet();

        Assert.All(expected, name => Assert.Contains(name, built));
    }

    [Fact]
    public void Discovery_finds_every_storage_options_type_the_application_has_today()
    {
        // Anti-vacuity: the guard discovers these by reflection; if discovery broke, the structural phase would
        // pass by checking nothing.
        Type[] known =
        [
            typeof(ServerStorageOptions), typeof(HostKeyTrustStorageOptions), typeof(RoutedHostKeyTrustStorageOptions),
            typeof(NotificationSettingsStorageOptions), typeof(BackgroundSettingsStorageOptions),
            typeof(WindowPlacementStorageOptions), typeof(HistoryStorageOptions), typeof(WidgetStateOptions),
            typeof(IgnoredDeviceStorageOptions)
        ];

        Assert.Subset(RealDataIsolationGuard.DiscoverStorageOptionsTypes().ToHashSet(), known.ToHashSet());
    }

    [Fact]
    public void Every_storage_options_type_the_production_root_registers_is_overridden()
    {
        var production = IsolatedAppComposition.ProductionDescriptors().Select(d => d.ServiceType).ToHashSet();
        var discovered = RealDataIsolationGuard.DiscoverStorageOptionsTypes().Where(production.Contains).ToList();

        // Vigil L-1C-2: an instance alone proves nothing - the production registration is an instance too. The
        // effective one must be a DIFFERENT value whose every path lies under the isolated root. Pure inspection:
        // the instances are read, nothing is resolved.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_composition.Root)) + Path.DirectorySeparatorChar;
        var productionInstances = IsolatedAppComposition.ProductionDescriptors()
            .Where(descriptor => descriptor.ImplementationInstance is not null)
            .GroupBy(descriptor => descriptor.ServiceType)
            .ToDictionary(group => group.Key, group => group.Last().ImplementationInstance!);

        Assert.NotEmpty(discovered);
        Assert.All(discovered, type =>
        {
            var effective = _composition.Services.Last(descriptor => descriptor.ServiceType == type).ImplementationInstance;
            Assert.NotNull(effective);
            if (productionInstances.TryGetValue(type, out var production))
            {
                Assert.NotEqual(production, effective);
            }

            var paths = RealDataIsolationGuard.PathProperties(type).Select(property => (string)property.GetValue(effective)!).ToList();
            Assert.NotEmpty(paths);
            Assert.All(paths, path => Assert.StartsWith(root, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void The_production_descriptors_cannot_be_built()
    {
        // Vigil M-1C-1: a read-only list, not an IServiceCollection - BuildServiceProvider has nothing to extend.
        var production = IsolatedAppComposition.ProductionDescriptors();

        Assert.False(typeof(IServiceCollection).IsAssignableFrom(production.GetType()));
        Assert.False(typeof(IServiceCollection).IsAssignableFrom(
            typeof(IsolatedAppComposition).GetMethod(nameof(IsolatedAppComposition.ProductionDescriptors))!.ReturnType));
        Assert.Throws<NotSupportedException>(() => ((IList<ServiceDescriptor>)production).Add(production[0]));
    }

    [Fact]
    public async Task A_storage_option_left_on_another_root_is_caught_before_anything_is_constructed()
    {
        _composition.Services.AddSingleton(new HistoryStorageOptions { DatabasePath = Path.Combine(_sentinel, "history.db") });
        await using var provider = _composition.Services.BuildServiceProvider();

        var violations = RealDataIsolationGuard.StructuralViolations(_composition.Services, provider, _composition.TestRoots);

        Assert.Contains(violations, v => v.Contains("HistoryStorageOptions.DatabasePath", StringComparison.Ordinal) && v.Contains(_sentinel, StringComparison.OrdinalIgnoreCase));
        Assert.Throws<Xunit.Sdk.TrueException>(() => _composition.BuildProvider().Dispose());
        Assert.False(Directory.Exists(_sentinel));
    }

    [Fact]
    public async Task An_ssh_profile_left_on_another_root_is_caught()
    {
        _composition.Services.AddSingleton<Core.Interfaces.ILocalSshKeyDiscovery>(_ => new Infrastructure.SshConfig.LocalSshKeyDiscovery(_sentinel));
        await using var provider = _composition.Services.BuildServiceProvider();

        var violations = RealDataIsolationGuard.StructuralViolations(_composition.Services, provider, _composition.TestRoots);

        Assert.Contains(violations, v => v.StartsWith("ILocalSshKeyDiscovery", StringComparison.Ordinal) && v.Contains(_sentinel, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_production_credential_store_registration_is_caught_without_being_constructed()
    {
        _composition.Services.AddSingleton<WindowsCredentialStore>();
        await using var provider = _composition.Services.BuildServiceProvider();

        var violations = RealDataIsolationGuard.StructuralViolations(_composition.Services, provider, _composition.TestRoots);

        Assert.Contains(violations, v => v.Contains("registered as the production type", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_store_built_on_another_root_without_an_options_registration_is_caught_by_the_graph_walk()
    {
        // Bypasses the options registrations entirely, so only the constructed phase can see it. The repository's
        // constructor performs no I/O; the sentinel directory is never created.
        _composition.Services.AddSingleton(sp => new JsonServerRepository(
            new ServerStorageOptions { FilePath = Path.Combine(_sentinel, "servers.json") },
            NullLogger<JsonServerRepository>.Instance,
            sp.GetRequiredService<IConfigurationWriteGate>()));
        await using var provider = _composition.BuildProvider();

        var violations = RealDataIsolationGuard.ConstructedViolations(_composition.Services, provider, _composition.TestRoots);

        Assert.Contains(violations, v => v.StartsWith("JsonServerRepository", StringComparison.Ordinal) && v.Contains(_sentinel, StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(_sentinel));
    }

    [Fact]
    public async Task The_credential_path_of_the_isolated_composition_ends_in_memory()
    {
        await using var provider = _composition.BuildProvider();

        Assert.Same(_composition.Credentials, provider.GetRequiredService<UngatedCredentialStore>().Store);
        Assert.Throws<RealDataTripwireException>(() => provider.GetRequiredService<WindowsCredentialStore>());

        var reference = Core.Security.CredentialReference.Create(Guid.NewGuid(), Core.Enums.ServerCredentialKind.Password);
        using (var secret = new Core.Security.SecretValue("synthetic"))
        {
            await provider.GetRequiredService<Core.Interfaces.IServerCredentialStore>().WriteAsync(reference, secret);
        }

        using var read = await _composition.Credentials.ReadAsync(reference);
        Assert.Equal("synthetic", read!.RevealAsString());
    }
}
