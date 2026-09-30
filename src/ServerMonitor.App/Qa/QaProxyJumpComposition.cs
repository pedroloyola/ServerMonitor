using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.App.Windowing;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY wiring for the M14.4b-2 ProxyJump editor visual QA. Launch with
/// <c>--qa-proxyjump --qa-proxyjump-dir=&lt;absolute dir&gt;</c>. Every file the app would write under
/// <c>%LOCALAPPDATA%\ServerMonitor</c> in this composition (servers, routed servers, both trust stores and the
/// three settings files) is re-rooted at that directory; secrets go to a process-local store instead of the
/// Credential Manager; monitoring and discovery are inert. The REAL <c>SshConnectionService</c>, server
/// service, profile service and trust stores run unchanged, so Test connection and the two-step trust flow
/// exercise production code against a loopback fixture. The mode is refused when the directory is missing.
/// Excluded from Release (see ServerMonitor.App.csproj); the flag is ignored there.
/// </summary>
internal static class QaProxyJumpComposition
{
    public static bool IsRequested() =>
        QaProxyJumpPolicy.IsRequested(Environment.GetCommandLineArgs(), isDebugBuild: true);

    /// <summary>The isolated directory, or an exception: never a fallback to the real profile.</summary>
    public static string RequiredDirectory() =>
        QaProxyJumpPolicy.ResolveDirectory(Environment.GetCommandLineArgs(), isDebugBuild: true)
        ?? throw new InvalidOperationException(
            $"{QaProxyJumpPolicy.LaunchFlag} requires {QaProxyJumpPolicy.DirectoryFlag}=<absolute path>; " +
            "refusing to start against the real profile.");

    /// <summary>Registered last so every registration here wins over the real one for every resolve.</summary>
    public static void Apply(IServiceCollection services, string directory)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("The QA data directory must be an absolute path.", nameof(directory));
        }

        var trust = new HostKeyTrustStorageOptions { FilePath = Path.Combine(directory, "known-hosts.json") };
        services.AddSingleton(new ServerStorageOptions { FilePath = Path.Combine(directory, "servers.json") });
        services.AddSingleton(trust);
        services.AddSingleton(RoutedHostKeyTrustStorageOptions.From(trust));
        services.AddSingleton(new BackgroundSettingsStorageOptions
        {
            FilePath = Path.Combine(directory, "background-settings.json")
        });
        services.AddSingleton(new NotificationSettingsStorageOptions
        {
            FilePath = Path.Combine(directory, "notification-settings.json")
        });
        services.AddSingleton(new WindowPlacementStorageOptions
        {
            FilePath = Path.Combine(directory, "window-placement.json")
        });

        services.AddSingleton<IServerCredentialStore, QaInMemoryCredentialStore>();

        // Inert data plane: nothing schedules, collects or discovers. The editor's Test connection calls
        // ISshConnectionService directly, which stays real.
        services.AddSingleton<IMonitoringEngine, QaMonitoringEngine>();
        services.AddSingleton<IServerDiscoveryService>(new QaDiscoveryService([]));
        services.AddSingleton<IServerMonitoringStateStore>(new ServerMonitoringStateStore());
    }
}
