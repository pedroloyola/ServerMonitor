namespace ServerMonitor.App.Services;

/// <summary>Platform boundary that displays an already-evaluated local notification.</summary>
public interface IUserNotificationService
{
    /// <summary>Synchronously rejects new delivery/activation work before hosted-service drain.</summary>
    void BeginShutdown() { }

    Task ShowAsync(UserNotification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows the single first-close background notice (M13 S2 §D.1). Separate from
    /// <see cref="ShowAsync"/> because it is not a health notification: it carries the background
    /// activation contract instead of the health one, is short-lived, and does not persist in the
    /// Notification Centre.
    /// </summary>
    void ShowBackgroundNotice(string title, string body) { }

    /// <summary>
    /// Shows the fail-safe exit notice (CV-17). Separate for the same reason as the background notice:
    /// it carries its own closed activation pair, and it is even shorter-lived.
    /// <para>
    /// <b>Fire and forget.</b> It returns nothing and is never awaited, because it is called from the
    /// committed exit path: the exit does not depend on it, does not wait for delivery, and does not
    /// learn whether the user ever saw it.
    /// </para>
    /// </summary>
    void ShowFailSafeExitNotice(string title, string body) { }
}
