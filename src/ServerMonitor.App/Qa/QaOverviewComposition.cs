using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

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

    public static void Apply(IServiceCollection services) =>
        Apply(services, RequestedScenario(), QaBackupScenarioComposition.RequestedScenario() is not null);

    /// <param name="backupDoublesRequested">
    /// UI.5 Cortex 4 / Boss B2 answer 5 (fail-closed, second line behind the launch refusal): whether <c>--qa-backup</c>
    /// is on the launch. Without it ANY scenario throws - the real backup engine and its native picker are never composed.
    /// </param>
    public static void Apply(IServiceCollection services, string scenarioName, bool backupDoublesRequested)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!backupDoublesRequested)
        {
            throw new InvalidOperationException(
                $"{QaOverviewScenarioPolicy.LaunchFlag} {scenarioName} requires {QaBackupPolicy.LaunchFlag} <scenario>: " +
                "Settings must never reach the real backup picker.");
        }

        var scenario = QaOverviewCatalog.Build(scenarioName);

        QaWindowPlacementIsolation.Apply(services, "overview");

        var stateStore = new ServerMonitoringStateStore();
        foreach (var entry in scenario.Servers)
        {
            stateStore.Set(entry.State);
        }

        var metrics = new QaOverviewMetricsStore(scenario);

        // Registered last so they win over the real registrations for every resolve.
        services.AddSingleton<IServerService>(new QaOverviewServerService(scenario));
        services.AddSingleton<IServerMetricsStore>(metrics);
        services.AddSingleton<IMonitoringEngine>(new QaOverviewMonitoringEngine(scenario, metrics, stateStore));
        services.AddSingleton<IServerDiscoveryService>(new QaDiscoveryService(scenario.Discovered));
        services.AddSingleton(new PresentationClock(new QaFixedTimeProvider(QaOverviewCatalog.Now)));
        services.AddSingleton<IServerMonitoringStateStore>(stateStore);
        // UI.5: synthetic CPU history for the Detail pulse (in memory; the real history stack is never composed in qaMode).
        services.AddSingleton<Core.History.IServerHistoryQueryService>(new QaOverviewHistoryQueryService(scenario));

        // UI.5: auth / host-key results live only in the connection-state store (the monitoring state collapses them to
        // Unknown + ConnectionFailed), so the harness seeds that store. Synthetic results: no key, no fingerprint.
        var connections = new ServerConnectionStateStore();
        foreach (var (serverId, state) in scenario.ConnectionStates ?? new Dictionary<Guid, ServerConnectionState>())
        {
            connections.Set(serverId, new SshConnectionResult { State = state });
        }

        services.AddSingleton<IServerConnectionStateStore>(connections);
    }
}
