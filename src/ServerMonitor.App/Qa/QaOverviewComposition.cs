using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY wiring for the UI.4 Visão geral / Servidores harness (<c>--qa-overview</c>, scenario via
/// <c>--qa-overview-scenario &lt;name&gt;</c>, default <see cref="QaOverviewScenarioPolicy.DefaultScenario"/>). Like
/// <see cref="QaHealthComposition"/>: the data plane is replaced with inert in-memory doubles seeded from
/// <see cref="QaOverviewCatalog"/>; the real engine, SSH, persistence and discovery are never registered (the composition
/// root guards them in qaMode) and <see cref="QaStartupIsolation"/> has already re-rooted every path. The overview's clock
/// is fixed (<see cref="PresentationClock"/> only — no container-wide TimeProvider). An unknown scenario is refused.
/// Excluded from Release.
/// </summary>
internal static class QaOverviewComposition
{
    public const string LaunchFlag = "--qa-overview";

    public static bool IsRequested() => IsRequested(Environment.GetCommandLineArgs());

    // Exact and ordinal, as THE harness parser (QaStartupIsolation.IsHarnessArgument).
    internal static bool IsRequested(IReadOnlyList<string> commandLineArgs) => commandLineArgs.Contains(LaunchFlag, StringComparer.Ordinal);

    /// <summary>The scenario the launch asked for; throws for an unknown value (never silently another scenario).</summary>
    public static string RequestedScenario() => RequestedScenario(Environment.GetCommandLineArgs());

    internal static string RequestedScenario(IReadOnlyList<string> commandLineArgs)
    {
        if (!QaOverviewScenarioPolicy.IsPresent(commandLineArgs))
        {
            return QaOverviewScenarioPolicy.DefaultScenario;
        }

        return QaOverviewScenarioPolicy.ResolveScenario(commandLineArgs, isDebugBuild: true)
            ?? throw new InvalidOperationException(
                $"{QaOverviewScenarioPolicy.LaunchFlag}: unknown scenario. Known: {string.Join(", ", QaOverviewScenarioPolicy.Scenarios)}.");
    }

    public static void Apply(IServiceCollection services) => Apply(services, RequestedScenario());

    public static void Apply(IServiceCollection services, string scenarioName)
    {
        ArgumentNullException.ThrowIfNull(services);
        var scenario = QaOverviewCatalog.Build(scenarioName);

        QaWindowPlacementIsolation.Apply(services, "overview");
        // Registered last so they win over the real registrations for every resolve.
        services.AddSingleton<IServerService>(new QaOverviewServerService(scenario));
        services.AddSingleton<IServerMetricsStore>(new QaOverviewMetricsStore(scenario));
        services.AddSingleton<IMonitoringEngine>(new QaMonitoringEngine());
        services.AddSingleton<IServerDiscoveryService>(new QaDiscoveryService(scenario.Discovered));
        services.AddSingleton(new PresentationClock(new QaFixedTimeProvider(QaOverviewCatalog.Now)));

        var stateStore = new ServerMonitoringStateStore();
        foreach (var entry in scenario.Servers)
        {
            stateStore.Set(entry.State);
        }

        services.AddSingleton<IServerMonitoringStateStore>(stateStore);
    }
}
