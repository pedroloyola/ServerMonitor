namespace ServerMonitor.WidgetContract;

/// <summary>
/// Normalized health carried on the widget wire. It deliberately mirrors the product's
/// <c>ServerHealth</c> semantics (§20) but is defined here independently so the widget provider needs
/// no Core reference. The writer maps the domain health onto this 1:1; neither side recomputes
/// thresholds, so the widget can never disagree with the dashboard for the same snapshot.
/// </summary>
public enum WidgetHealth
{
    /// <summary>No usable data yet, or a non-transient problem needing the user's attention.</summary>
    Unknown = 0,

    /// <summary>Reachable and every available metric is within warning thresholds.</summary>
    Healthy = 1,

    /// <summary>Reachable but at least one metric crossed its warning threshold.</summary>
    Warning = 2,

    /// <summary>Reachable but at least one metric crossed its critical threshold.</summary>
    Critical = 3,

    /// <summary>Could not reach the server after the retry policy was exhausted.</summary>
    Offline = 4
}
