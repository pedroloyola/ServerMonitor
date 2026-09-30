namespace ServerMonitor.Core.Backup;

/// <summary>
/// Every way a configuration backup export, inspect or restore can fail (M14.6 spec §7). Logs and UI
/// carry only this code, counts and exception type names — never parser, codec or credential-store
/// messages, which may quote content (Vigil C-6).
/// </summary>
public enum BackupError
{
    /// <summary>The file does not start with the backup magic.</summary>
    NotABackup,

    /// <summary>The file is structurally invalid (size, lengths, version 0, iteration bounds).</summary>
    Damaged,

    /// <summary>The file was written by a newer or different format this version cannot read.</summary>
    IncompatibleVersion,

    /// <summary>Authentication failed: the passphrase is wrong, or the file was damaged or modified.
    /// The two cannot be told apart and are deliberately reported as one message (spec §6.4).</summary>
    WrongPassphraseOrDamaged,

    /// <summary>The decrypted payload failed strict parsing or domain validation.</summary>
    InvalidContent,

    /// <summary>The file or the payload exceeds the format's size limits.</summary>
    TooLarge,

    CredentialStoreUnavailable,

    TrustStoreUnreadable,

    WriteFailed,

    RolledBack,

    PartialRestoreRollbackPending,

    /// <summary>The platform cannot run the format's primitives (AES-GCM unavailable).</summary>
    Unsupported,

    Canceled,
}
