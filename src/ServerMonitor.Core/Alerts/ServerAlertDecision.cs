using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Alerts;

/// <summary>An alert-worthy transition evaluated by <see cref="ServerAlertPolicy"/>.</summary>
public sealed record ServerAlertDecision(
    ServerAlertCategory Category,
    ServerHealth PreviousHealth,
    ServerHealth CurrentHealth);
