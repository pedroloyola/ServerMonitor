using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Backup;
using ServerMonitor.Infrastructure.Backup;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.App.Services;

/// <summary>
/// Startup restore recovery (M14.6 §5.2), run from <c>OnLaunched</c> before any configuration store or the engine
/// exists. Recovery may roll real configuration files back or forward and delete real credentials, and §5.2 is safe
/// only because a single instance means no concurrent writer. It therefore runs ONLY when this launch owns the
/// single-instance key (Cortex-2).
/// <para>
/// Every Debug <c>--qa-*</c> launch bypasses the key, so it could run next to a normal instance that is mid-Apply;
/// such a launch never recovers anything. That includes the isolated harnesses: each backs up into its own
/// per-process (or QA-owned) directory, so there is never a journal of theirs to finish at the next launch. A QA
/// journal left behind stays blocked by C-9 inside that QA directory only; that is the documented choice.
/// </para>
/// </summary>
public static class StartupRestoreRecovery
{
    /// <summary>True only when the launch holds the single-instance key (production always; Debug unless --qa-*).</summary>
    public static bool ShouldRecover(IReadOnlyList<string> commandLineArgs, bool isDebugBuild) =>
        SingleInstancePolicy.ResolveInstanceKey(commandLineArgs, isDebugBuild) is not null;

    public static async Task<RestoreRecoveryReport> RunAsync(
        IServiceProvider services,
        IReadOnlyList<string> commandLineArgs,
        bool isDebugBuild)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(commandLineArgs);

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(StartupRestoreRecovery));
        var servers = services.GetRequiredService<ServerStorageOptions>();
        var journalDirectory = Path.Combine(Path.GetDirectoryName(servers.FilePath) ?? string.Empty, "restore-journal");
        RestoreRecoveryReport report;

        if (!ShouldRecover(commandLineArgs, isDebugBuild))
        {
            logger.LogInformation("Restore recovery skipped: this launch does not own the single-instance key.");
            report = RestoreRecoveryReport.None(journalDirectory);
            services.GetRequiredService<RestoreRecoveryStatus>().Report = report;
            return report;
        }

        try
        {
            report = await RestoreJournalRecovery.RecoverAsync(
                servers,
                services.GetRequiredService<HostKeyTrustStorageOptions>(),
                services.GetRequiredService<RoutedHostKeyTrustStorageOptions>(),
                services.GetRequiredService<NotificationSettingsStorageOptions>().FilePath,
                services.GetRequiredService<BackgroundSettingsStorageOptions>().FilePath,
                services.GetRequiredService<UngatedCredentialStore>().Store,
                logger);
        }
        catch (Exception exception)
        {
            // Never block startup; the journal stays and backup/restore remain blocked (C-9).
            logger.LogError("Restore recovery could not run ({ExceptionType}).", exception.GetType().Name);
            report = new RestoreRecoveryReport(RestoreRecoveryOutcome.Stuck, journalDirectory);
        }

        services.GetRequiredService<RestoreRecoveryStatus>().Report = report;
        services.GetRequiredService<OrphanTemporaryCleaner>().CleanRestoreJournalTemporaries(
            RestoreJournalRecovery.JournalTemporaryFiles(report.JournalDirectory));
        return report;
    }
}
