using System.IO;
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


/// <summary>
/// QA-ONLY scenario doubles for the M14.6 backup/restore UI (distinct from <see cref="QaBackupComposition"/>,
/// which runs the REAL engine over isolated stores). Launch with <c>--qa-backup &lt;scenario&gt;</c>
/// (see <see cref="QaBackupPolicy.Scenarios"/>), together with an isolated data harness such as
/// <c>--qa-health</c>, to walk every dialog and outcome. The engine and both pickers are in-memory
/// doubles: nothing is encrypted, read or written, and no native file dialog opens.
/// The passphrase the double accepts is <see cref="QaBackupService.AcceptedPassphrase"/>.
/// Excluded from Release (see ServerMonitor.App.csproj); the flag is ignored there.
/// </summary>
internal static class QaBackupScenarioComposition
{
    public static string? RequestedScenario() =>
        QaBackupPolicy.ResolveScenario(Environment.GetCommandLineArgs(), isDebugBuild: true);

    /// <summary>Registered last so it wins over the real registrations for every resolve.</summary>
    public static void Apply(IServiceCollection services, string scenario)
    {
        services.AddSingleton<IConfigurationBackupService>(new QaBackupService(scenario));
        services.AddSingleton<IBackupFilePicker, QaBackupFilePicker>();
    }
}

/// <summary>Returns fixed paths under the temp folder without opening a dialog or touching a file.</summary>
internal sealed class QaBackupFilePicker : IBackupFilePicker
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "serveralyzer-qa-backup");

    public Task<string?> PickSaveAsync(
        string suggestedFileName,
        string fileTypeLabel,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(Path.Combine(Folder, suggestedFileName));

    public Task<string?> PickOpenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(Path.Combine(Folder, "home-lab.serveralyzer-backup"));
}

internal sealed class QaBackupService(string scenario) : IConfigurationBackupService
{
    public const string AcceptedPassphrase = "correct horse battery staple";

    private static readonly string Journal = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ServerMonitor",
        "restore-journal") + Path.DirectorySeparatorChar;

    public RestoreRecoveryReport StartupRecovery { get; } = scenario switch
    {
        "stuck" => new RestoreRecoveryReport(RestoreRecoveryOutcome.Stuck, Journal),
        "recovered" => new RestoreRecoveryReport(RestoreRecoveryOutcome.RolledBack, Journal),
        _ => RestoreRecoveryReport.None(Journal)
    };

    public async Task<BackupExportResult> ExportAsync(
        string destinationPath,
        ReadOnlyMemory<char> passphrase,
        ReadOnlyMemory<char> confirmation,
        CancellationToken cancellationToken = default)
    {
        var problem = BackupPassphrasePolicy.ValidateForExport(passphrase.Span, confirmation.Span);
        if (problem != BackupPassphraseProblem.None)
        {
            return new BackupExportResult { PassphraseProblem = problem };
        }

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        return scenario switch
        {
            "stuck" => new BackupExportResult { Error = BackupError.RestorePending, PendingJournalDirectory = Journal },
            "invalid" => new BackupExportResult { Error = BackupError.WriteFailed },
            _ => new BackupExportResult
            {
                Summary = new BackupExportSummary
                {
                    DirectServers = 4,
                    RoutedServers = 2,
                    Credentials = 3,
                    DirectTrustedHostKeys = 4,
                    RoutedTrustedHostKeys = 2,
                    ExcludedUnreferencedTrustedHostKeys = 1,
                    MissingCredentials = [new BackupCredentialFlag(Guid.NewGuid(), "backup-nas", IsJump: false)]
                }
            }
        };
    }

    public async Task<RestoreInspectResult> InspectAsync(
        string sourcePath,
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken = default)
    {
        var problem = BackupPassphrasePolicy.ValidateForRestore(passphrase.Span);
        if (problem != BackupPassphraseProblem.None)
        {
            return new RestoreInspectResult { PassphraseProblem = problem };
        }

        var accepted = passphrase.Span.SequenceEqual(AcceptedPassphrase);
        await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
        if (scenario == "stuck")
        {
            return new RestoreInspectResult { Error = BackupError.RestorePending, PendingJournalDirectory = Journal };
        }

        if (scenario == "invalid")
        {
            return new RestoreInspectResult { Error = BackupError.NotABackup };
        }

        return accepted
            ? new RestoreInspectResult { Plan = new QaRestorePlan(Summary()) }
            : new RestoreInspectResult { Error = BackupError.WrongPassphraseOrDamaged };
    }

    public async Task<RestoreApplyResult> ApplyAsync(RestorePlan plan, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(plan.IsDisposed, plan);
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        return scenario switch
        {
            "rollback" => new RestoreApplyResult { Outcome = RestoreApplyOutcome.RolledBack, Error = BackupError.WriteFailed },
            "partial" => new RestoreApplyResult
            {
                Outcome = RestoreApplyOutcome.PartialRestoreRollbackPending,
                Error = BackupError.CredentialStoreUnavailable,
                JournalDirectory = Journal
            },
            _ => new RestoreApplyResult { Outcome = RestoreApplyOutcome.Completed }
        };
    }

    private static RestoreSummary Summary() => new()
    {
        BackupCreatedAt = DateTimeOffset.Now.AddDays(-3),
        BackupAppVersion = "1.2.0",
        Backup = new RestoreCounts(4, 2, 3, 4, 2),
        Current = new RestoreCounts(5, 1, 4, 7, 1),
        DirectTrustedHostKeysToRemove = 3,
        RoutedTrustedHostKeysToRemove = 0,
        BackupSettings = new PortableSettings(NotificationsEnabled: false, BackgroundMonitoringEnabled: true),
        CurrentSettings = new PortableSettings(NotificationsEnabled: true, BackgroundMonitoringEnabled: true),
        MissingCredentials = [new BackupCredentialFlag(Guid.NewGuid(), "backup-nas", IsJump: false)],
        KeyPathWarnings =
        [
            new KeyPathWarning(Guid.NewGuid(), "prod-web-01", IsJump: false, KeyPathStatus.Missing),
            new KeyPathWarning(Guid.NewGuid(), "db-internal", IsJump: true, KeyPathStatus.NotChecked)
        ]
    };

    private sealed class QaRestorePlan(RestoreSummary summary) : RestorePlan(summary)
    {
        private bool _disposed;

        public override bool IsDisposed => _disposed;

        protected override void Dispose(bool disposing) => _disposed = true;
    }
}
