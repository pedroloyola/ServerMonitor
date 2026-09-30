using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Qa;
using ServerMonitor.App.Services;
using ServerMonitor.App.Windowing;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// The Debug-only --qa-proxyjump harness: layered on the REAL composition root, every per-user file the
/// editor path can write resolves inside the isolated directory, and the credential store the real
/// profile service receives is the process-local one, never the Credential Manager.
/// </summary>
public sealed class QaProxyJumpHarnessTests
{
    private static readonly string IsolatedDirectory =
        Path.Combine(Path.GetTempPath(), "servermonitor-qa-proxyjump-tests");

    private static readonly string RealProfileDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServerMonitor");

    [Fact]
    public void HarnessIsNotRequestedByDefault()
    {
        Assert.False(QaProxyJumpComposition.IsRequested());
    }

    [Fact]
    public void WithoutTheDirectory_TheModeIsRefused()
    {
        Assert.Throws<InvalidOperationException>(QaProxyJumpComposition.RequiredDirectory);
    }

    [Fact]
    public void Apply_RejectsARelativeDirectory()
    {
        Assert.Throws<ArgumentException>(() => QaProxyJumpComposition.Apply(new ServiceCollection(), @"relative\dir"));
    }

    [Fact]
    public void OnTheRealRoot_EveryPerUserFileResolvesInsideTheIsolatedDirectory()
    {
        using var provider = BuildHarnessProvider();

        var paths = new Dictionary<string, string>
        {
            ["servers"] = provider.GetRequiredService<ServerStorageOptions>().FilePath,
            ["routed-servers"] = provider.GetRequiredService<ServerStorageOptions>().RoutedFilePath,
            ["known-hosts"] = provider.GetRequiredService<HostKeyTrustStorageOptions>().FilePath,
            ["known-hosts.routes"] = provider.GetRequiredService<RoutedHostKeyTrustStorageOptions>().FilePath,
            ["background"] = provider.GetRequiredService<BackgroundSettingsStorageOptions>().FilePath,
            ["notifications"] = provider.GetRequiredService<NotificationSettingsStorageOptions>().FilePath,
            ["placement"] = provider.GetRequiredService<WindowPlacementStorageOptions>().FilePath,
        };

        Assert.All(paths, pair =>
        {
            Assert.Equal(IsolatedDirectory, Path.GetDirectoryName(pair.Value));
            Assert.False(
                pair.Value.StartsWith(RealProfileDirectory, StringComparison.OrdinalIgnoreCase),
                $"{pair.Key} resolved into the real profile: {pair.Value}");
        });
    }

    [Fact]
    public void OnTheRealRoot_TheProfileServiceGetsTheProcessLocalCredentialStore()
    {
        var services = BuildHarnessServices();

        var winning = services.Last(descriptor => descriptor.ServiceType == typeof(IServerCredentialStore));
        Assert.Equal(typeof(QaInMemoryCredentialStore), winning.ImplementationType);

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IServerCredentialStore>();
        Assert.IsType<QaInMemoryCredentialStore>(store);
        Assert.IsNotType<WindowsCredentialStore>(store);

        // The real consumers resolve through the same singleton: nothing in the editor path can reach the
        // Credential Manager registration, which stays in the collection but is shadowed.
        Assert.NotNull(provider.GetRequiredService<IServerProfileService>());
        Assert.Same(store, provider.GetRequiredService<IServerCredentialStore>());
    }

    [Fact]
    public async Task TheProcessLocalStore_RoundTripsAndOwnsItsCopy()
    {
        var store = new QaInMemoryCredentialStore();
        var reference = CredentialReference.Create(Guid.NewGuid(), ServerCredentialKind.Password);

        using (var secret = new SecretValue("synthetic"))
        {
            await store.WriteAsync(reference, secret);
        }

        using (var read = await store.ReadAsync(reference))
        {
            Assert.Equal("synthetic", read!.RevealAsString());
        }

        Assert.True(await store.DeleteAsync(reference));
        Assert.Null(await store.ReadAsync(reference));
    }

    private static IServiceCollection BuildHarnessServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        App.ConfigureApplicationServices(services);
        QaProxyJumpComposition.Apply(services, IsolatedDirectory);
        return services;
    }

    private static ServiceProvider BuildHarnessProvider() => BuildHarnessServices().BuildServiceProvider();
}
