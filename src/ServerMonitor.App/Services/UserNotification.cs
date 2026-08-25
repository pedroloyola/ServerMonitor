using ServerMonitor.Core.Alerts;

namespace ServerMonitor.App.Services;

/// <summary>
/// Sanitized local-notification content. It deliberately carries no endpoint, credential,
/// fingerprint, SSH error or metric payload.
/// </summary>
public sealed record UserNotification(
    Guid ServerId,
    ServerAlertCategory Category,
    string Title,
    string Body);
