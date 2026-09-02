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
}
