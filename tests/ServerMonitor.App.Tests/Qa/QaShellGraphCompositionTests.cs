using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Qa;
using ServerMonitor.App.Tests.TestSupport;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// UI10-A1 (Cortex root cause): the composition root registers <see cref="IServerLoadStatusSource"/> as a hard cast of
/// <see cref="IServerService"/> (fail-closed, bcf340c), and <c>MainWindow</c> needs it through
/// <see cref="OnboardingViewModel"/>. A --qa-* harness whose server double does not implement the interface threw
/// <see cref="InvalidCastException"/> while the window was resolved, and OnLaunched's startup catch exited 0 with no
/// window - six harnesses broke silently that way. This resolves the shell graph's non-UI head for EVERY Debug harness,
/// layered on the real composition root exactly as App does (over the isolated test root), so a double without the
/// interface fails here, by name, instead of at launch.
/// </summary>
public sealed class QaShellGraphCompositionTests : IDisposable
{
    private const string Exe = "ServerMonitor.App.exe";

    private readonly IsolatedAppComposition _composition = new();

    private string HarnessDirectory => Path.Combine(_composition.Root, "qa-harness");

    public void Dispose() => _composition.Dispose();

    public static TheoryData<string> EveryHarness() =>
        ["health", "discovery", "notifications", "history", "workloads", "screenshot", "compact", "overview", "editor", "proxyjump"];

    [Theory]
    [MemberData(nameof(EveryHarness))]
    public async Task EveryHarness_ResolvesTheLoadStatusSourceAndTheOnboardingViewModel(string harness)
    {
        var services = _composition.Services;
        switch (harness)
        {
            case "health": QaHealthComposition.Apply(services); break;
            case "discovery": QaDiscoveryComposition.Apply(services); break;
            case "notifications": QaNotificationComposition.Apply(services); break;
            case "history": QaHistoryComposition.Apply(services); break;
            case "workloads": QaWorkloadsComposition.Apply(services); break;
            case "screenshot": QaStoreScreenshotComposition.Apply(services); break;
            case "compact": QaCompactComposition.Apply(services, new QaCompactLaunch(QaCompactCatalog.DefaultScenario, ServerMonitor.App.Windowing.WindowMode.Compact, null)); break;
            case "overview": QaOverviewComposition.Apply(services, "mixed", backupDoublesRequested: true); break;
            case "editor":
                QaStartupIsolation.Apply(services, Path.Combine(HarnessDirectory, "editor"), rerootSshProfile: true);
                QaEditorComposition.Apply(services, [Exe, "--qa-editor", "--qa-backup", "ok"]);
                break;
            default: QaProxyJumpComposition.Apply(services, HarnessDirectory); break;
        }

        if (harness != "proxyjump")
            QaBackupComposition.ApplyIsolated(services, HarnessDirectory);
        await using var provider = _composition.BuildProvider();

        var server = provider.GetRequiredService<IServerService>();
        var source = provider.GetRequiredService<IServerLoadStatusSource>();
        Assert.Same(server, source);
        Assert.NotNull(provider.GetRequiredService<OnboardingViewModel>());
    }

    [Fact]
    public async Task Discovery_ReportsAnHonestEmptyFleet_EveryOtherSixHarnessLoaded()
    {
        foreach (var (apply, expected) in new (Action<IServiceCollection> Apply, ServerLoadStatus Expected)[]
                 {
                     (QaDiscoveryComposition.Apply, ServerLoadStatus.NotFound),
                     (QaHealthComposition.Apply, ServerLoadStatus.Loaded),
                     (QaNotificationComposition.Apply, ServerLoadStatus.Loaded),
                     (QaHistoryComposition.Apply, ServerLoadStatus.Loaded),
                     (QaWorkloadsComposition.Apply, ServerLoadStatus.Loaded),
                     (QaStoreScreenshotComposition.Apply, ServerLoadStatus.Loaded),
                 })
        {
            using var composition = new IsolatedAppComposition();
            apply(composition.Services);
            QaBackupComposition.ApplyIsolated(composition.Services, Path.Combine(composition.Root, "qa-harness"));
            await using var provider = composition.BuildProvider();
            Assert.Equal(expected, await provider.GetRequiredService<IServerLoadStatusSource>().GetLoadStatusAsync());
        }
    }
}
