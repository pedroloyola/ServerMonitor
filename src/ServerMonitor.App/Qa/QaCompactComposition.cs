using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Windowing;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Qa;

/// <summary>What a <c>--qa-compact</c> launch asked for, once the strict parser accepted it.</summary>
internal sealed record QaCompactLaunch(string Scenario, WindowMode StartMode, QaCompactTickerMode? Ticker);

/// <summary>
/// QA-ONLY wiring for the UI.8 Compact Mode harness. <c>--qa-compact</c> (exact) selects it; three strict modifiers, each
/// at most once as <c>flag value</c> or <c>flag=value</c> and only next to <c>--qa-compact</c>:
/// <list type="bullet">
/// <item><c>--qa-compact-scenario &lt;name&gt;</c> - one of <see cref="QaCompactCatalog.Scenarios"/> (default
/// <see cref="QaCompactCatalog.DefaultScenario"/>);</item>
/// <item><c>--qa-compact-start compact|standard</c> - the mode the window opens in (default compact), to exercise the
/// entries and transitions;</item>
/// <item><c>--qa-compact-ticker identical|varying</c> - a deterministic ticker that republishes the scenario's states
/// through the REAL monitoring-state store (<see cref="QaCompactTicker"/>).</item>
/// </list>
/// Anything else - an unknown scenario, a malformed value, a repeated modifier, the retired <c>--qa-compact:N</c> count
/// form - is refused at launch (exit 3), never replaced by a default or clamped. The data plane is inert and in memory,
/// the presentation clock is fixed (<see cref="QaCompactCatalog.Now"/>), and the window placement is an in-memory store
/// that is never written to disk; <see cref="QaStartupIsolation"/> has already re-rooted every other path. Excluded
/// from Release.
/// </summary>
internal static class QaCompactComposition
{
    public const string LaunchFlag = "--qa-compact";
    public const string ScenarioFlag = "--qa-compact-scenario";
    public const string StartFlag = "--qa-compact-start";
    public const string TickerFlag = "--qa-compact-ticker";

    private static readonly string[] Modifiers = [ScenarioFlag, StartFlag, TickerFlag];

    public static bool IsRequested() => IsRequested(Environment.GetCommandLineArgs());

    // Exact and ordinal, as THE harness parser (QaStartupIsolation.IsHarnessArgument).
    internal static bool IsRequested(IReadOnlyList<string> commandLineArgs) => commandLineArgs.Contains(LaunchFlag, StringComparer.Ordinal);

    /// <summary>True when the launch asked for the ticker (the composition root then registers it as a hosted service).</summary>
    public static bool TickerRequested() => Parse(Environment.GetCommandLineArgs()) is { Ticker: not null };

    /// <summary>
    /// Null when the compact modifiers of this launch are acceptable; otherwise why the launch is refused. A launch
    /// without any compact modifier is not this parser's business (null).
    /// </summary>
    internal static string? Refusal(IReadOnlyList<string> commandLineArgs)
    {
        ArgumentNullException.ThrowIfNull(commandLineArgs);
        var present = Modifiers.Where(flag => Occurrences(commandLineArgs, flag) > 0).ToList();
        if (present.Count == 0)
        {
            return null;
        }

        if (!IsRequested(commandLineArgs))
        {
            return $"{string.Join(", ", present)} only applies to {LaunchFlag}.";
        }

        return TryParse(commandLineArgs, out _, out var refusal) ? null : refusal;
    }

    /// <summary>The accepted launch, or null when the arguments are not an acceptable compact launch.</summary>
    internal static QaCompactLaunch? Parse(IReadOnlyList<string> commandLineArgs) =>
        IsRequested(commandLineArgs) && TryParse(commandLineArgs, out var launch, out _) ? launch : null;

    private static bool TryParse(IReadOnlyList<string> args, out QaCompactLaunch launch, out string refusal)
    {
        launch = new QaCompactLaunch(QaCompactCatalog.DefaultScenario, WindowMode.Compact, null);
        refusal = string.Empty;

        // Fail-closed on its own too: any other --qa-compact* switch (a typo of a modifier, the retired ':N' form).
        var stray = args.FirstOrDefault(argument => argument.StartsWith(LaunchFlag, StringComparison.OrdinalIgnoreCase)
            && argument != LaunchFlag
            && !Modifiers.Any(flag => argument == flag || argument.StartsWith(flag + "=", StringComparison.Ordinal)));
        if (stray is not null)
        {
            refusal = $"'{stray}' is not a {LaunchFlag} switch (modifiers: {string.Join(", ", Modifiers)}).";
            return false;
        }

        if (!TryValue(args, ScenarioFlag, out var scenario, ref refusal)
            || !TryValue(args, StartFlag, out var start, ref refusal)
            || !TryValue(args, TickerFlag, out var ticker, ref refusal))
        {
            return false;
        }

        if (scenario is not null && !QaCompactCatalog.IsKnown(scenario))
        {
            refusal = $"{ScenarioFlag} needs one of: {string.Join(", ", QaCompactCatalog.Scenarios)}.";
            return false;
        }

        WindowMode? startMode = start switch
        {
            null or "compact" => WindowMode.Compact,
            "standard" => WindowMode.Standard,
            _ => null
        };
        if (startMode is null)
        {
            refusal = $"{StartFlag} needs one of: compact, standard.";
            return false;
        }

        QaCompactTickerMode? tickerMode = ticker switch
        {
            "identical" => QaCompactTickerMode.Identical,
            "varying" => QaCompactTickerMode.Varying,
            _ => null
        };
        if (ticker is not null && tickerMode is null)
        {
            refusal = $"{TickerFlag} needs one of: identical, varying.";
            return false;
        }

        launch = new QaCompactLaunch(scenario ?? QaCompactCatalog.DefaultScenario, startMode.Value, tickerMode);
        return true;
    }

    // At most once; 'flag value' (the next argument, which must not itself be a switch) or 'flag=value'; never empty.
    private static bool TryValue(IReadOnlyList<string> args, string flag, out string? value, ref string refusal)
    {
        value = null;
        var occurrences = Occurrences(args, flag);
        if (occurrences == 0)
        {
            return true;
        }

        if (occurrences > 1)
        {
            refusal = $"{flag} may appear only once.";
            return false;
        }

        for (var index = 0; index < args.Count; index++)
        {
            if (args[index] == flag)
            {
                value = index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;
                break;
            }

            if (args[index].StartsWith(flag + "=", StringComparison.Ordinal))
            {
                value = args[index][(flag.Length + 1)..];
                break;
            }
        }

        if (string.IsNullOrEmpty(value))
        {
            refusal = $"{flag} needs a value.";
            return false;
        }

        return true;
    }

    private static int Occurrences(IReadOnlyList<string> args, string flag) =>
        args.Count(argument => argument == flag || argument.StartsWith(flag + "=", StringComparison.Ordinal));

    /// <summary>Composes the launch's own arguments; throws for a launch the strict parser refuses.</summary>
    public static void Apply(IServiceCollection services) =>
        Apply(services, Parse(Environment.GetCommandLineArgs())
            ?? throw new InvalidOperationException($"{LaunchFlag}: refused launch ({Refusal(Environment.GetCommandLineArgs())})."));

    public static void Apply(IServiceCollection services, QaCompactLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(launch);
        var catalog = QaCompactCatalog.Build(launch.Scenario);
        var metrics = new QaCompactMetricsStore(catalog);
        var states = new ServerMonitoringStateStore();
        foreach (var entry in catalog.Entries)
        {
            states.Set(entry.State);
        }

        // Registered last so they win over the real registrations for every resolve.
        services.AddSingleton(catalog);
        services.AddSingleton<IServerService>(new QaCompactServerService(catalog));
        services.AddSingleton<IServerMetricsStore>(metrics);
        services.AddSingleton<IMonitoringEngine, QaMonitoringEngine>();
        services.AddSingleton<IServerDiscoveryService>(new QaDiscoveryService([]));
        services.AddSingleton<IServerMonitoringStateStore>(states);
        services.AddSingleton(new PresentationClock(new QaFixedTimeProvider(QaCompactCatalog.Now)));

        // The window opens in the requested mode without reading or writing the real placement file.
        services.AddSingleton<IWindowPlacementStore>(new QaCompactPlacementStore(launch.StartMode));

        if (launch.Ticker is { } mode)
        {
            services.AddSingleton(new QaCompactTicker(catalog, metrics, states, mode, TimeProvider.System));
        }
    }
}

/// <summary>
/// QA-ONLY read-only <see cref="IServerService"/> over the compact catalogue (hidden servers included, so the all-hidden
/// state is real). The <c>loading</c> scenario's load never completes; <c>config-unavailable</c> reports the configuration
/// as unavailable to the startup diagnosis. Nothing is ever persisted; add/edit are unsupported.
/// </summary>
internal sealed class QaCompactServerService(QaCompactCatalog catalog) : IServerService, IServerLoadStatusSource
{
    private readonly TaskCompletionSource<IReadOnlyList<Server>> _neverCompletes = new();

    public event EventHandler? ServersChanged { add { } remove { } }

    public Task<ServerLoadStatus> GetLoadStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(catalog.LoadStatus);

    public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
        catalog.NeverLoads ? _neverCompletes.Task : Task.FromResult(catalog.Servers);

    public Task<ServerOperationResult> AddAsync(ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA compact harness is read-only.");

    public Task<ServerOperationResult> AddAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA compact harness is read-only.");

    public Task<ServerOperationResult> UpdateAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA compact harness is read-only.");

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

/// <summary>QA-ONLY in-memory snapshots per server (null = no data); the ticker replaces them in the varying mode.</summary>
internal sealed class QaCompactMetricsStore(QaCompactCatalog catalog) : IServerMetricsStore
{
    private readonly ConcurrentDictionary<Guid, ServerMetricsSnapshot> _snapshots = new(
        catalog.Entries.Where(entry => entry.Snapshot is not null)
            .Select(entry => KeyValuePair.Create(entry.Server.Id, entry.Snapshot!)));

    public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) => _snapshots.GetValueOrDefault(serverId);

    internal void Replace(ServerMetricsSnapshot snapshot) => _snapshots[snapshot.ServerId] = snapshot;

    public Task<ServerMetricsCollectionResult> RefreshAsync(Server server, CancellationToken cancellationToken = default) =>
        Task.FromResult(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Unexpected));

    public void Remove(Guid serverId)
    {
        // No-op: QA snapshots are in-memory and keyed by the synthetic id.
    }
}

/// <summary>
/// QA-ONLY placement store: opens in the requested mode and keeps every later change IN MEMORY for the session (so
/// Standard ⇄ Compact round trips behave as in production), but never reads or writes a file.
/// </summary>
internal sealed class QaCompactPlacementStore(WindowMode startMode) : IWindowPlacementStore
{
    private WindowPlacementSettings _settings = WindowPlacementSettings.Default with { Mode = startMode };

    public WindowPlacementSettings Load() => _settings;

    public void Save(WindowPlacementSettings settings) => _settings = settings;
}
