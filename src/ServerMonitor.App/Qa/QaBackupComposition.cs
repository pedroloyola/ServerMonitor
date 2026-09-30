using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Domain;
using ServerMonitor.Infrastructure.Backup;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY: the backup/restore service for every Debug <c>--qa-*</c> harness whose composition does not already
/// isolate the whole data plane (M14.6 Addendum 2). The REAL engine runs, but over its own stores in a
/// per-process temp directory (servers, both trust files, both settings files, the restore journal) and a
/// process-local credential store — never the real <c>servers.json</c>, the real known-hosts files or the
/// Windows Credential Manager. It shares the app's configuration gate, so an Apply still locks the UI's
/// ordinary writers exactly as in production. Excluded from Release (see ServerMonitor.App.csproj).
/// </summary>
internal static class QaBackupComposition
{
    /// <summary>The per-process isolated directory used when none is given.</summary>
    public static string DefaultDirectory() => Path.Combine(
        Path.GetTempPath(),
        "ServerMonitor-qa-backup",
        Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Registered last so it wins over the production registration for every resolve.</summary>
    public static void ApplyIsolated(IServiceCollection services, string directory)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("The QA backup directory must be an absolute path.", nameof(directory));
        }

        services.AddSingleton(sp => new IsolatedBackup(sp, directory));
        services.AddSingleton<IConfigurationBackupService>(sp => sp.GetRequiredService<IsolatedBackup>().Service);
    }

    /// <summary>Owns the isolated stores so the container disposes them with the service.</summary>
    internal sealed class IsolatedBackup : IDisposable
    {
        private readonly JsonServerRepository _repository;
        private readonly ServerService _servers;
        private readonly JsonHostKeyTrustStore _trust;
        private readonly JsonRoutedHostKeyTrustStore _routedTrust;

        public IsolatedBackup(IServiceProvider services, string directory)
        {
            System.IO.Directory.CreateDirectory(directory);
            Directory = directory;
            var gate = services.GetRequiredService<IConfigurationWriteGate>();
            var loggers = services.GetRequiredService<ILoggerFactory>();
            var serverOptions = new ServerStorageOptions { FilePath = Path.Combine(directory, "servers.json") };
            var trustOptions = new HostKeyTrustStorageOptions { FilePath = Path.Combine(directory, "known-hosts.json") };
            var routedTrustOptions = RoutedHostKeyTrustStorageOptions.From(trustOptions);
            var notificationOptions = new NotificationSettingsStorageOptions { FilePath = Path.Combine(directory, "notification-settings.json") };
            var backgroundOptions = new BackgroundSettingsStorageOptions { FilePath = Path.Combine(directory, "background-settings.json") };

            _repository = new JsonServerRepository(serverOptions, loggers.CreateLogger<JsonServerRepository>(), gate);
            _servers = new ServerService(_repository, new ServerValidator(), gate);
            _trust = new JsonHostKeyTrustStore(trustOptions, loggers.CreateLogger<JsonHostKeyTrustStore>(), gate);
            _routedTrust = new JsonRoutedHostKeyTrustStore(routedTrustOptions, loggers.CreateLogger<JsonRoutedHostKeyTrustStore>(), gate);
            var settings = new PortableSettingsParticipant(
                notificationOptions,
                backgroundOptions,
                new JsonNotificationSettingsService(notificationOptions, loggers.CreateLogger<JsonNotificationSettingsService>(), gate),
                new JsonBackgroundMonitoringSettingsService(backgroundOptions, loggers.CreateLogger<JsonBackgroundMonitoringSettingsService>(), gate));

            RawCredentials = new QaInMemoryCredentialStore();
            Service = new ConfigurationBackupService(
                _servers,
                new ServerValidator(),
                _repository,
                _trust,
                _routedTrust,
                new UngatedCredentialStore(RawCredentials),
                settings,
                gate,
                services.GetRequiredService<IRestoreMonitoringControl>(),
                serverOptions,
                trustOptions,
                routedTrustOptions,
                services.GetRequiredService<BackupServiceOptions>(),
                new RestoreRecoveryStatus(),
                loggers.CreateLogger<ConfigurationBackupService>());
        }

        public string Directory { get; }

        public QaInMemoryCredentialStore RawCredentials { get; }

        public ConfigurationBackupService Service { get; }

        public void Dispose()
        {
            _servers.Dispose();
            _repository.Dispose();
            _trust.Dispose();
            _routedTrust.Dispose();
        }
    }
}
