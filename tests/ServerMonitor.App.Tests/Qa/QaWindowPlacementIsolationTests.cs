using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Qa;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// UI.1.7 "harnesses must not write real data": no Debug <c>--qa-*</c> harness may resolve a placement store
/// that reads or writes the user's real <c>%LOCALAPPDATA%\ServerMonitor\window-placement.json</c>. Each case
/// reproduces the production registration order from App.xaml.cs (real options + JSON store first, harness
/// last) and asserts what the window would actually get.
/// </summary>
public sealed class QaWindowPlacementIsolationTests
{
    private static readonly string RealAppDataRoot = Path.GetDirectoryName(WindowPlacementStorageOptions.ForCurrentUser().FilePath)!;

    public static TheoryData<string> Harnesses =>
        ["health", "discovery", "notifications", "history", "workloads", "screenshot", "compact", "proxyjump"];

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void HarnessNeverResolvesTheRealPlacementFile(string harness)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Production registrations, exactly as App.xaml.cs makes them before the QA composition runs.
        services.AddSingleton(WindowPlacementStorageOptions.ForCurrentUser());
        services.AddSingleton<JsonWindowPlacementStore>();
        services.AddSingleton<IWindowPlacementStore>(sp => sp.GetRequiredService<JsonWindowPlacementStore>());

        Apply(harness, services);

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IWindowPlacementStore>();
        if (store is JsonWindowPlacementStore)
        {
            // The real store runs; only its file is re-rooted, and never under the real app-data folder.
            var path = provider.GetRequiredService<WindowPlacementStorageOptions>().FilePath;
            Assert.False(IsUnderRealAppData(path), $"--qa-{harness} writes the real placement file: {path}");
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // --qa-compact swaps the store itself for an in-memory double.
            Assert.Equal(typeof(QaCompactComposition).Namespace, store.GetType().Namespace);
        }
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void NoStorageOptionRegisteredByTheHarnessPointsAtRealAppData(string harness)
    {
        var services = new ServiceCollection();
        Apply(harness, services);

        var paths = services
            .Select(descriptor => descriptor.ImplementationInstance)
            .Where(instance => instance is not null && instance.GetType().Name.EndsWith("StorageOptions", StringComparison.Ordinal))
            .SelectMany(instance => instance!.GetType().GetProperties()
                .Where(property => property.PropertyType == typeof(string) && property.Name.EndsWith("Path", StringComparison.Ordinal))
                .Select(property => (string?)property.GetValue(instance)))
            .OfType<string>()
            .ToList();

        Assert.DoesNotContain(paths, IsUnderRealAppData);
    }

    private static bool IsUnderRealAppData(string path) =>
        Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(RealAppDataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void Apply(string harness, IServiceCollection services)
    {
        switch (harness)
        {
            case "health": QaHealthComposition.Apply(services); break;
            case "discovery": QaDiscoveryComposition.Apply(services); break;
            case "notifications": QaNotificationComposition.Apply(services); break;
            case "history": QaHistoryComposition.Apply(services); break;
            case "workloads": QaWorkloadsComposition.Apply(services); break;
            case "screenshot": QaStoreScreenshotComposition.Apply(services); break;
            case "compact": QaCompactComposition.Apply(services); break;
            case "proxyjump":
                QaProxyJumpComposition.Apply(services, Path.Combine(Path.GetTempPath(), "ServerMonitor-QA-tests", "proxyjump"));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(harness), harness, null);
        }
    }
}
