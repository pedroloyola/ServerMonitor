namespace ServerMonitor.App.Services;

/// <summary>Platform boundary that displays an already-evaluated local notification.</summary>
public interface IUserNotificationService
{
    /// <summary>Synchronously rejects new delivery/activation work before hosted-service drain.</summary>
    void BeginShutdown() { }

    Task ShowAsync(UserNotification notification, CancellationToken cancellationToken = default);
}
