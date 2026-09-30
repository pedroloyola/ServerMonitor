using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Backup;

/// <summary>The credential <c>kind</c> strings of the backup payload and the restore journal (the same
/// names as the Credential Manager target names).</summary>
public static class BackupCredentialKindNames
{
    public static string ToName(ServerCredentialKind kind) => kind switch
    {
        ServerCredentialKind.Password => "password",
        ServerCredentialKind.PrivateKeyPassphrase => "key-passphrase",
        ServerCredentialKind.JumpPassword => "jump-password",
        ServerCredentialKind.JumpPrivateKeyPassphrase => "jump-key-passphrase",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static bool TryParse(string? name, out ServerCredentialKind kind)
    {
        kind = name switch
        {
            "password" => ServerCredentialKind.Password,
            "key-passphrase" => ServerCredentialKind.PrivateKeyPassphrase,
            "jump-password" => ServerCredentialKind.JumpPassword,
            "jump-key-passphrase" => ServerCredentialKind.JumpPrivateKeyPassphrase,
            _ => 0
        };
        return kind != 0;
    }
}

/// <summary>
/// The portable settings files (notification + background), provided by the App so Infrastructure never
/// references it (spec §7). Restore replaces each file whole; the background file keeps the target's current
/// one-shot notice flag.
/// </summary>
public interface IPortableSettingsParticipant
{
    string NotificationSettingsFilePath { get; }

    string BackgroundSettingsFilePath { get; }

    PortableSettings ReadCurrent();

    byte[] RenderNotificationSettings(PortableSettings settings);

    byte[] RenderBackgroundSettings(PortableSettings settings);

    /// <summary>Settings as the services would load them from these bytes (restore verification).</summary>
    PortableSettings ReadNotificationAndBackground(byte[]? notificationBytes, byte[]? backgroundBytes);

    /// <summary>Restore-only write of the notification file (<paramref name="content"/> null = delete it),
    /// under the service's own lock; the service reloads its in-memory state from the file.</summary>
    void ReplaceNotificationSettings(RestoreWriteToken token, byte[]? content);

    void ReplaceBackgroundSettings(RestoreWriteToken token, byte[]? content);
}

/// <summary>Stops monitoring for the duration of a restore and restarts it after a rollback (App adapter
/// over the monitoring engine).</summary>
public interface IRestoreMonitoringControl
{
    Task StopAsync(CancellationToken cancellationToken);

    Task ResumeAsync(CancellationToken cancellationToken);
}
