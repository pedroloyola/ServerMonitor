namespace ServerMonitor.Core.Backup;

/// <summary>
/// Every way a configuration backup export, inspect or restore can fail (M14.6 spec §7). Logs and UI
/// carry only this code, counts and exception type names — never parser, codec or credential-store
/// messages, which may quote content (Vigil C-6). Passphrase-policy failures are reported separately as
/// <see cref="BackupPassphraseProblem"/>. New members are appended so existing values never change.
/// </summary>
public enum BackupError
{
    /// <summary>The file does not start with the backup magic.</summary>
    NotABackup,

    /// <summary>The file is structurally invalid (size, lengths, version 0, iteration bounds).</summary>
    Damaged,

    /// <summary>The file or its payload was written by a newer or different format this version cannot read.</summary>
    IncompatibleVersion,

    /// <summary>Authentication failed: the passphrase is wrong, or the file was damaged or modified.
    /// The two cannot be told apart and are deliberately reported as one message (spec §6.4).</summary>
    WrongPassphraseOrDamaged,

    /// <summary>The decrypted payload failed strict parsing or domain validation (restore), or the current
    /// configuration holds values a backup cannot represent (export).</summary>
    InvalidContent,

    /// <summary>The file or the payload exceeds the format's size limits.</summary>
    TooLarge,

    /// <summary>The Windows Credential Manager failed (not "credential missing").</summary>
    CredentialStoreUnavailable,

    /// <summary>A trusted-host-key file is invalid or unreadable, so export would silently drop trust.</summary>
    TrustStoreUnreadable,

    /// <summary>Writing a file failed.</summary>
    WriteFailed,

    RolledBack,

    PartialRestoreRollbackPending,

    /// <summary>The platform cannot run the format's primitives (AES-GCM unavailable).</summary>
    Unsupported,

    Canceled,

    /// <summary>A previous restore journal is pending; backup and restore are blocked until it is resolved.</summary>
    RestorePending,

    /// <summary>A configuration write did not finish in time (restore), or a restore is running (export).</summary>
    Busy,

    /// <summary>The selected backup file could not be opened or read (missing, access denied, I/O error).</summary>
    ReadFailed,
}
