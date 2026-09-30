using ServerMonitor.Core.Backup;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// The one place a backup/restore code becomes a resource key (M14.6). The UI shows only localized text
/// keyed by these codes; no exception, parser or credential-store text ever reaches it.
/// <see langword="null"/> means "say nothing".
/// </summary>
public static class BackupMessageKeys
{
    /// <summary>For a code the operation cannot produce: claims only that nothing was lost.</summary>
    public const string Generic = "ServerOperationError.Message";

    public const string Blocked = "RestorePendingBlocked";

    public const string ConfigurationLocked = "ConfigurationLocked";

    public static string? ForPassphraseProblem(BackupPassphraseProblem problem) => problem switch
    {
        BackupPassphraseProblem.None => null,
        BackupPassphraseProblem.Empty => "PassphraseEmpty",
        BackupPassphraseProblem.TooShort => "PassphraseTooShort",
        BackupPassphraseProblem.TooLong => "PassphraseTooLong",
        BackupPassphraseProblem.InvalidCharacters => "PassphraseInvalidCharacters",
        BackupPassphraseProblem.ConfirmationMismatch => "PassphraseMismatch",
        _ => Generic
    };

    public static string? ForExportError(BackupError error) => error switch
    {
        BackupError.WriteFailed => "BackupFailedWrite",
        BackupError.CredentialStoreUnavailable => "BackupFailedCredentialStore",
        BackupError.TrustStoreUnreadable => "BackupFailedTrustStore",
        BackupError.TooLarge => "BackupFailedTooLarge",
        BackupError.InvalidContent => "BackupFailedInvalidContent",
        BackupError.Unsupported => "BackupUnsupported",
        BackupError.Busy => ConfigurationLocked,
        BackupError.RestorePending => Blocked,
        BackupError.Canceled => null,
        _ => Generic
    };

    public static string? ForInspectError(BackupError error) => error switch
    {
        BackupError.NotABackup => "RestoreErrorNotABackup",
        BackupError.Damaged => "RestoreErrorDamaged",
        BackupError.IncompatibleVersion => "RestoreErrorIncompatible",
        BackupError.WrongPassphraseOrDamaged => "RestoreErrorWrongPassphraseOrDamaged",
        BackupError.InvalidContent => "RestoreErrorInvalidContent",
        BackupError.TooLarge => "RestoreErrorTooLarge",
        BackupError.ReadFailed => "RestoreErrorFileAccess",
        BackupError.Unsupported => "BackupUnsupported",
        BackupError.Busy => "RestoreBusy",
        BackupError.RestorePending => Blocked,
        BackupError.Canceled => null,
        _ => Generic
    };

    public static string ForApplyOutcome(RestoreApplyOutcome outcome) => outcome switch
    {
        RestoreApplyOutcome.Completed => "RestoreCompletedMessage",
        RestoreApplyOutcome.RolledBack => "RestoreRolledBackMessage",
        RestoreApplyOutcome.Canceled => "RestoreCanceled",
        RestoreApplyOutcome.PartialRestoreRollbackPending => "RestorePartialPendingMessage",
        RestoreApplyOutcome.Busy => "RestoreBusy",
        RestoreApplyOutcome.RestorePending => Blocked,
        _ => Generic
    };

    public static string? ForStartupRecovery(RestoreRecoveryOutcome outcome) => outcome switch
    {
        RestoreRecoveryOutcome.NothingToRecover => null,
        RestoreRecoveryOutcome.RolledBack => "RestoreRecoveredUndone",
        RestoreRecoveryOutcome.Completed => "RestoreRecoveredCompleted",
        RestoreRecoveryOutcome.Stuck => Blocked,
        _ => null
    };

    public static string ForKeyPathStatus(KeyPathStatus status) => status switch
    {
        KeyPathStatus.Missing => "RestoreSummaryMissingKeys",
        KeyPathStatus.NotChecked => "RestoreSummaryNetworkKeys",
        KeyPathStatus.Unsupported => "RestoreSummaryUnsupportedKeys",
        _ => Generic
    };
}
