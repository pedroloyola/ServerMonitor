namespace ServerMonitor.Core.Alerts;

/// <summary>
/// User-facing alert categories derived from monitoring health transitions. These are
/// intentionally independent from any Windows notification API.
/// </summary>
public enum ServerAlertCategory
{
    Warning,
    Critical,
    Offline,
    Recovery
}
