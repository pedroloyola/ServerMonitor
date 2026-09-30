using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Qa;
using ServerMonitor.Core.Backup;
using ServerMonitor.Infrastructure.Backup;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// M14.6 Addendum 2: <see cref="IConfigurationBackupService"/> resolves in the production composition AND in
/// every Debug --qa-* harness, and no harness can reach the real profile or the Credential Manager through it.
/// Each harness is layered on the REAL composition root, exactly as App does.
/// </summary>
public sealed class QaBackupCompositionTests : IDisposable
{
    private static readonly string RealProfile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServerMonitor");

    private readonly string _isolated = Path.Combine(Path.GetTempPath(), "sm-qa-backup-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_isolated, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Production_ResolvesTheRealServiceOverTheRealProfile()
    {
        var services = Root();
        await using var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IConfigurationBackupService>();

        Assert.IsType<ConfigurationBackupService>(service);
        Assert.StartsWith(RealProfile, service.StartupRecovery.JournalDirectory, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string> IsolatedHarnesses() =>
        ["health", "notifications", "compact", "history", "workloads", "screenshot", "discovery", "ssh-config"];

    [Theory]
    [MemberData(nameof(IsolatedHarnesses))]
    public async Task EveryHarness_ResolvesTheService_IsolatedFromTheRealProfileAndCredentialManager(string harness)
    {
        var services = Root();
        switch (harness)
        {
            case "health": QaHealthComposition.Apply(services); break;
            case "notifications": QaNotificationComposition.Apply(services); break;
            case "compact": QaCompactComposition.Apply(services); break;
            case "history": QaHistoryComposition.Apply(services); break;
            case "workloads": QaWorkloadsComposition.Apply(services); break;
            case "screenshot": QaStoreScreenshotComposition.Apply(services); break;
            case "discovery": QaDiscoveryComposition.Apply(services); break;
            default: QaSshConfigComposition.Apply(services, _isolated); break;
        }

        QaBackupComposition.ApplyIsolated(services, _isolated);
        await using var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IConfigurationBackupService>();
        var isolated = provider.GetRequiredService<QaBackupComposition.IsolatedBackup>();

        Assert.Same(isolated.Service, service);
        Assert.Equal(Path.Combine(_isolated, "restore-journal"), service.StartupRecovery.JournalDirectory);
        Assert.IsType<QaInMemoryCredentialStore>(isolated.RawCredentials);

        // The app's gate is shared, so an Apply still locks the UI's ordinary writers.
        var token = await provider.GetRequiredService<IConfigurationWriteGate>().BeginRestoreAsync(TimeSpan.FromSeconds(1));
        Assert.NotNull(token);
        provider.GetRequiredService<IConfigurationWriteGate>().Release(token);
    }

    [Fact]
    public async Task ProxyJumpHarness_ResolvesTheRealWiring_OverItsIsolatedDirectoryAndInMemoryStore()
    {
        var services = Root();
        QaProxyJumpComposition.Apply(services, _isolated);
        await using var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IConfigurationBackupService>();

        Assert.IsType<ConfigurationBackupService>(service);
        Assert.Equal(Path.Combine(_isolated, "restore-journal"), service.StartupRecovery.JournalDirectory);
        Assert.IsType<QaInMemoryCredentialStore>(provider.GetRequiredService<UngatedCredentialStore>().Store);
    }

    private static ServiceCollection Root()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        App.ConfigureApplicationServices(services);
        return services;
    }
}
