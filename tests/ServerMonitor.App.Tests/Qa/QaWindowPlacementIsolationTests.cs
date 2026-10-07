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
        ["health", "discovery", "notifications", "history", "workloads", "screenshot", "compact", "proxyjump", "overview"];

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

    /// <summary>T-13 (UI.2 G-5): a placement file left by an earlier run is gone after Apply, so every run starts from the default.</summary>
    [Fact]
    public void ApplyStartsFromAFreshPlacement()
    {
        const string harness = "t13-fresh-placement";
        var directory = QaWindowPlacementIsolation.DirectoryFor(harness);
        Directory.CreateDirectory(directory);
        var stale = Path.Combine(directory, QaWindowPlacementIsolation.FileName);
        File.WriteAllText(stale, """{ "Mode": "Compact" }""");

        var services = new ServiceCollection();
        QaWindowPlacementIsolation.Apply(services, harness);

        Assert.False(File.Exists(stale), "A stale placement file survived Apply: the run would not start from the default.");
        using var provider = services.BuildServiceProvider();
        Assert.Equal(stale, provider.GetRequiredService<WindowPlacementStorageOptions>().FilePath);
    }

    /// <summary>
    /// The delete is fail-closed to the QA root. SAFETY: this test NEVER passes a real user path - not even to prove a
    /// refusal - because a regression in the guard would then delete real data (it did once, during a UI.2
    /// counterproof). It uses a sentinel file outside the QA root, inside the test's own temp folder, and asserts it
    /// both throws and leaves the sentinel in place.
    /// </summary>
    [Fact]
    public void StaleDeleteRefusesAnyPathOutsideTheQaRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ServerMonitor-QA-guard-sentinel");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, QaWindowPlacementIsolation.FileName);
        File.WriteAllText(sentinel, "sentinel");
        var escaping = Path.Combine(QaWindowPlacementIsolation.Root, "..", "ServerMonitor-QA-guard-sentinel", QaWindowPlacementIsolation.FileName);

        Assert.Throws<InvalidOperationException>(() => QaWindowPlacementIsolation.DeleteStalePlacement(sentinel));
        Assert.Throws<InvalidOperationException>(() => QaWindowPlacementIsolation.DeleteStalePlacement(escaping));
        Assert.True(File.Exists(sentinel), "The guard let a delete escape the QA root.");
        Assert.False(Path.GetFullPath(sentinel).StartsWith(Path.GetFullPath(RealAppDataRoot), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>G-5: the Store screenshot harness uses a fixed in-memory placement that never persists.</summary>
    [Fact]
    public void StoreScreenshotPlacementIsFixedAndNeverPersisted()
    {
        // SAFETY: a temp path stands in for the production options, so even a regressed composition cannot write real data.
        var services = new ServiceCollection();
        services.AddSingleton(new WindowPlacementStorageOptions { FilePath = Path.Combine(Path.GetTempPath(), "ServerMonitor-QA-tests", "placement", QaWindowPlacementIsolation.FileName) });
        services.AddSingleton<JsonWindowPlacementStore>();
        services.AddSingleton<IWindowPlacementStore>(sp => sp.GetRequiredService<JsonWindowPlacementStore>());
        QaStoreScreenshotComposition.Apply(services);

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IWindowPlacementStore>();
        Assert.IsType<QaInMemoryPlacementStore>(store);
        store.Save(new WindowPlacementSettings { Mode = WindowMode.Compact });
        Assert.Equal(WindowPlacementSettings.Default, store.Load());
    }

    /// <summary>Vigil F-10: the suite runs under its own QA root, never the live root that running harnesses share.</summary>
    [Fact]
    public void TestsUseAnIsolatedQaRoot()
    {
        var live = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ServerMonitor-QA"));
        Assert.Equal(QaTestRoots.Root, QaWindowPlacementIsolation.Root);
        Assert.False(Path.GetFullPath(QaWindowPlacementIsolation.Root).StartsWith(live + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(live, Path.GetFullPath(QaWindowPlacementIsolation.Root), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Cortex R-9: a junction inside the QA root must not redirect the delete. SAFETY: the junction targets a temp
    /// sentinel folder created by this test - never a real path - and the test asserts the sentinel file survives.
    /// </summary>
    [Fact]
    public void StaleDeleteRefusesToFollowAJunction()
    {
        var target = Path.Combine(Path.GetTempPath(), "ServerMonitor-QA-junction-sentinel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, QaWindowPlacementIsolation.FileName);
        File.WriteAllText(sentinel, "sentinel");
        var link = QaWindowPlacementIsolation.DirectoryFor("junction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        CreateJunction(link, target);
        try
        {
            var throughLink = Path.Combine(link, QaWindowPlacementIsolation.FileName);
            Assert.True(QaPathSafety.CrossesReparsePoint(throughLink));
            Assert.Throws<InvalidOperationException>(() => QaWindowPlacementIsolation.DeleteStalePlacement(throughLink));
            Assert.True(File.Exists(sentinel), "The delete followed the junction.");
            Assert.False(QaPathSafety.CrossesReparsePoint(sentinel));
        }
        finally
        {
            Directory.Delete(link); // removes the junction only, never its target's contents
        }
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        })!;
        process.WaitForExit(10_000);
        Assert.True(Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "Could not create the test junction.");
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
            case "compact": QaCompactComposition.Apply(services, new QaCompactLaunch(QaCompactCatalog.DefaultScenario, WindowMode.Compact, null)); break;
            case "overview": QaOverviewComposition.Apply(services, "mixed", backupDoublesRequested: true); break;
            case "proxyjump":
                QaProxyJumpComposition.Apply(services, Path.Combine(Path.GetTempPath(), "ServerMonitor-QA-tests", "proxyjump"));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(harness), harness, null);
        }
    }
}
