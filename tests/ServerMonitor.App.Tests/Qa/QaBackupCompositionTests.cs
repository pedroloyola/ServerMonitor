using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Qa;
using ServerMonitor.App.Tests.TestSupport;
using ServerMonitor.Core.Backup;
using ServerMonitor.Infrastructure.Backup;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// M14.6 Addendum 2: <see cref="IConfigurationBackupService"/> resolves in the production composition AND in
/// every Debug --qa-* harness, and no harness can reach the real profile or the Credential Manager through it.
/// Each harness is layered on the REAL composition root, exactly as App does.
/// <para>
/// TEST-REALDATA-AUDIT: the root under each harness is <see cref="IsolatedAppComposition"/>, so whatever a harness
/// does NOT override (the engine graph behind IRestoreMonitoringControl, for one) still lands in the temp root, and
/// the harness directory is a subdirectory of it. What these tests prove is what each harness's backup wiring
/// asserts below; they do not prove the --qa-* RUNTIME isolates the rest of the data plane (that is the
/// QaStartupIsolation work, outside this file).
/// </para>
/// </summary>
public sealed class QaBackupCompositionTests : IDisposable
{
    private static readonly string RealProfile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServerMonitor");

    private readonly IsolatedAppComposition _composition = new();

    private string HarnessDirectory => Path.Combine(_composition.Root, "qa-harness");

    public void Dispose() => _composition.Dispose();

    [Fact]
    public async Task Production_RegistersTheRealProfile_AndTheRealServiceDerivesItsJournalFromIt()
    {
        // The production root targets the real profile: inspected on the descriptor, never resolved.
        var production = IsolatedAppComposition.ProductionDescriptors();
        var servers = Assert.IsType<ServerStorageOptions>(
            production.Last(descriptor => descriptor.ServiceType == typeof(ServerStorageOptions)).ImplementationInstance);
        Assert.StartsWith(RealProfile, servers.FilePath, StringComparison.OrdinalIgnoreCase);

        // The real service it wires derives its journal from that root - proven over the isolated root.
        await using var provider = _composition.BuildProvider();
        var service = provider.GetRequiredService<IConfigurationBackupService>();

        Assert.IsType<ConfigurationBackupService>(service);
        Assert.Equal(Path.Combine(_composition.DataDirectory, "restore-journal"), service.StartupRecovery.JournalDirectory);
    }

    public static TheoryData<string> IsolatedHarnesses() =>
        ["health", "notifications", "compact", "history", "workloads", "screenshot", "discovery", "overview", "ssh-config"];

    [Theory]
    [MemberData(nameof(IsolatedHarnesses))]
    public async Task EveryHarness_ResolvesTheService_IsolatedFromTheRealProfileAndCredentialManager(string harness)
    {
        var services = _composition.Services;
        switch (harness)
        {
            case "health": QaHealthComposition.Apply(services); break;
            case "notifications": QaNotificationComposition.Apply(services); break;
            case "compact": QaCompactComposition.Apply(services); break;
            case "history": QaHistoryComposition.Apply(services); break;
            case "workloads": QaWorkloadsComposition.Apply(services); break;
            case "screenshot": QaStoreScreenshotComposition.Apply(services); break;
            case "discovery": QaDiscoveryComposition.Apply(services); break;
            case "overview": QaOverviewComposition.Apply(services, "mixed"); break;
            default: QaSshConfigComposition.Apply(services, HarnessDirectory); break;
        }

        QaBackupComposition.ApplyIsolated(services, HarnessDirectory);
        await using var provider = _composition.BuildProvider();

        var service = provider.GetRequiredService<IConfigurationBackupService>();
        var isolated = provider.GetRequiredService<QaBackupComposition.IsolatedBackup>();

        Assert.Same(isolated.Service, service);
        Assert.Equal(Path.Combine(HarnessDirectory, "restore-journal"), service.StartupRecovery.JournalDirectory);
        Assert.IsType<QaInMemoryCredentialStore>(isolated.RawCredentials);

        // The app's gate is shared, so an Apply still locks the UI's ordinary writers.
        var token = await provider.GetRequiredService<IConfigurationWriteGate>().BeginRestoreAsync(TimeSpan.FromSeconds(1));
        Assert.NotNull(token);
        provider.GetRequiredService<IConfigurationWriteGate>().Release(token);
    }

    [Fact]
    public async Task ProxyJumpHarness_ResolvesTheRealWiring_OverItsIsolatedDirectoryAndInMemoryStore()
    {
        QaProxyJumpComposition.Apply(_composition.Services, HarnessDirectory);
        await using var provider = _composition.BuildProvider();

        var service = provider.GetRequiredService<IConfigurationBackupService>();

        Assert.IsType<ConfigurationBackupService>(service);
        Assert.Equal(Path.Combine(HarnessDirectory, "restore-journal"), service.StartupRecovery.JournalDirectory);
        Assert.IsType<QaInMemoryCredentialStore>(provider.GetRequiredService<UngatedCredentialStore>().Store);
    }
}
