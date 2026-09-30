namespace ServerMonitor.Core.Backup;

/// <summary>
/// Encrypted configuration backup and REPLACE restore (M14.6). Community feature: no entitlement check
/// anywhere on this path (Vigil C-12). Every failure is a <see cref="BackupError"/> or a
/// <see cref="BackupPassphraseProblem"/>; no method surfaces parser, codec or credential-store text.
/// </summary>
public interface IConfigurationBackupService
{
    /// <summary>What startup recovery did with an interrupted restore, to be surfaced once by the UI.</summary>
    RestoreRecoveryReport StartupRecovery { get; }

    /// <summary>
    /// Writes an encrypted backup to <paramref name="destinationPath"/>: unique temp file in the same folder →
    /// verify → move (a previous file at the destination survives any failure). The passphrase is checked with
    /// <see cref="BackupPassphrasePolicy.ValidateForExport"/> against <paramref name="confirmation"/> first.
    /// </summary>
    Task<BackupExportResult> ExportAsync(
        string destinationPath,
        ReadOnlyMemory<char> passphrase,
        ReadOnlyMemory<char> confirmation,
        CancellationToken cancellationToken = default);

    /// <summary>Decrypts and fully validates a backup in memory. Touches nothing.</summary>
    Task<RestoreInspectResult> InspectAsync(
        string sourcePath,
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Destructive REPLACE of servers, credentials, trusted host keys and portable settings. Any failure before
    /// the commit point leaves the configuration exactly as it was. On <see cref="RestoreApplyOutcome.Completed"/>
    /// the app must close (ordinary configuration writes stay refused until the process exits).
    /// The caller still owns and disposes <paramref name="plan"/>.
    /// </summary>
    Task<RestoreApplyResult> ApplyAsync(RestorePlan plan, CancellationToken cancellationToken = default);
}

public sealed record BackupExportResult
{
    public bool Succeeded => Error is null && PassphraseProblem == BackupPassphraseProblem.None;

    public BackupError? Error { get; init; }

    public BackupPassphraseProblem PassphraseProblem { get; init; }

    /// <summary>Set on success.</summary>
    public BackupExportSummary? Summary { get; init; }

    /// <summary>Set when <see cref="Error"/> is <see cref="BackupError.RestorePending"/> (N4).</summary>
    public string? PendingJournalDirectory { get; init; }
}

public sealed record BackupExportSummary
{
    public int DirectServers { get; init; }

    public int RoutedServers { get; init; }

    public int Credentials { get; init; }

    public int DirectTrustedHostKeys { get; init; }

    public int RoutedTrustedHostKeys { get; init; }

    /// <summary>Trusted host keys not used by any exported server, deliberately left out (H-4).</summary>
    public int ExcludedUnreferencedTrustedHostKeys { get; init; }

    /// <summary>Stored server entries that could not be read or are invalid, so were not included.</summary>
    public int ExcludedUnreadableServers { get; init; }

    /// <summary>Servers whose saved password/passphrase was not found; exported without it.</summary>
    public IReadOnlyList<BackupCredentialFlag> MissingCredentials { get; init; } = [];
}

public sealed record RestoreInspectResult
{
    public bool Succeeded => Plan is not null;

    public BackupError? Error { get; init; }

    public BackupPassphraseProblem PassphraseProblem { get; init; }

    /// <summary>Set on success. The caller owns it and must dispose it on every exit path.</summary>
    public RestorePlan? Plan { get; init; }

    /// <summary>Set when <see cref="Error"/> is <see cref="BackupError.RestorePending"/> (N4).</summary>
    public string? PendingJournalDirectory { get; init; }
}

/// <summary>
/// A validated, re-keyed restore held in memory (it contains secrets). Disposing it zeroes them. Only the
/// service that created it can apply it.
/// </summary>
public abstract class RestorePlan : IDisposable
{
    protected RestorePlan(RestoreSummary summary)
    {
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
    }

    public RestoreSummary Summary { get; }

    public abstract bool IsDisposed { get; }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected abstract void Dispose(bool disposing);

    public override string ToString() => nameof(RestorePlan);
}

/// <summary>Everything the destructive confirm must show (spec §6.8, V10): current vs backup.</summary>
public sealed record RestoreSummary
{
    public DateTimeOffset BackupCreatedAt { get; init; }

    public string BackupAppVersion { get; init; } = string.Empty;

    public required RestoreCounts Backup { get; init; }

    public required RestoreCounts Current { get; init; }

    /// <summary>Current direct trusted host keys that are not in the backup and will be removed;
    /// <see langword="null"/> when the current trust file cannot be read.</summary>
    public int? DirectTrustedHostKeysToRemove { get; init; }

    /// <summary>Same for routed (jump) trusted host keys.</summary>
    public int? RoutedTrustedHostKeysToRemove { get; init; }

    public required PortableSettings BackupSettings { get; init; }

    public required PortableSettings CurrentSettings { get; init; }

    /// <summary>Servers restored without a saved password/passphrase (the user re-enters it).</summary>
    public IReadOnlyList<BackupCredentialFlag> MissingCredentials { get; init; } = [];

    public IReadOnlyList<KeyPathWarning> KeyPathWarnings { get; init; } = [];
}

/// <param name="Credentials">Saved passwords/passphrases referenced by the servers.</param>
/// <param name="DirectTrustedHostKeys"><see langword="null"/> when that trust file cannot be read.</param>
public sealed record RestoreCounts(
    int DirectServers,
    int RoutedServers,
    int Credentials,
    int? DirectTrustedHostKeys,
    int? RoutedTrustedHostKeys);

/// <summary>The portable settings a backup carries (spec D7).</summary>
public sealed record PortableSettings(bool NotificationsEnabled, bool BackgroundMonitoringEnabled);

/// <param name="IsJump">The flag concerns the server's jump host, not the server itself.</param>
public sealed record BackupCredentialFlag(Guid ServerId, string ServerName, bool IsJump);

public enum KeyPathStatus
{
    /// <summary>A local path whose file is not on this computer.</summary>
    Missing,

    /// <summary>A local path that is a link, directory or other non-file: it will not be usable.</summary>
    Unsupported,

    /// <summary>A network/device/unsupported path: never checked (no network access), will not be usable.</summary>
    NotChecked,
}

public sealed record KeyPathWarning(Guid ServerId, string ServerName, bool IsJump, KeyPathStatus Status);

public enum RestoreApplyOutcome
{
    /// <summary>Committed. The app must close; configuration writes are refused until it does.</summary>
    Completed,

    /// <summary>Failed before the commit point and fully undone. <see cref="RestoreApplyResult.Error"/> is the cause.</summary>
    RolledBack,

    /// <summary>Canceled before the commit point and fully undone.</summary>
    Canceled,

    /// <summary>Failed and could not be fully undone; the next start finishes undoing it (journal kept).</summary>
    PartialRestoreRollbackPending,

    /// <summary>Another configuration write did not finish in time. Nothing was touched.</summary>
    Busy,

    /// <summary>A previous restore journal is still pending. Nothing was touched.</summary>
    RestorePending,
}

public sealed record RestoreApplyResult
{
    public required RestoreApplyOutcome Outcome { get; init; }

    /// <summary>The cause for <see cref="RestoreApplyOutcome.RolledBack"/> /
    /// <see cref="RestoreApplyOutcome.PartialRestoreRollbackPending"/>; otherwise the outcome's own code.</summary>
    public BackupError? Error { get; init; }

    /// <summary>Set for <see cref="RestoreApplyOutcome.PartialRestoreRollbackPending"/> and
    /// <see cref="RestoreApplyOutcome.RestorePending"/> (N4).</summary>
    public string? JournalDirectory { get; init; }

    /// <summary>Completed, but some replaced credentials could not be deleted yet; the next start retries.</summary>
    public bool OldCredentialCleanupPending { get; init; }
}

public enum RestoreRecoveryOutcome
{
    NothingToRecover,

    /// <summary>A restore was interrupted before its commit point and has been undone.</summary>
    RolledBack,

    /// <summary>A restore had committed; its clean-up was finished.</summary>
    Completed,

    /// <summary>A journal exists that recovery could not process safely; nothing destructive was done.
    /// Backup and restore stay blocked until <see cref="RestoreRecoveryReport.JournalDirectory"/> is removed
    /// by hand, which abandons any automatic undo (N4).</summary>
    Stuck,
}

public sealed record RestoreRecoveryReport(RestoreRecoveryOutcome Outcome, string JournalDirectory)
{
    public static RestoreRecoveryReport None(string journalDirectory) =>
        new(RestoreRecoveryOutcome.NothingToRecover, journalDirectory);
}
