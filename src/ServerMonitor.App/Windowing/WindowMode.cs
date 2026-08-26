namespace ServerMonitor.App.Windowing;

/// <summary>
/// The two presentation modes of the single application window. Compact is the in-process
/// widget presentation described in ADR-005 / ADR-014 — it never implies a second window,
/// process, monitoring engine or discovery service.
/// </summary>
public enum WindowMode
{
    Standard = 0,
    Compact = 1
}
