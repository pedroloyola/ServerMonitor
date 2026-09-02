namespace ServerMonitor.App.Services;

/// <summary>
/// Persistent user preference for the background lifecycle (M13 S2 §D.1), plus the one-shot record of
/// whether the first-close notice has already been dealt with.
/// <para>
/// Two booleans, both non-sensitive, in their own file — the same shape as
/// <see cref="INotificationSettingsService"/>. It carries no server, host or fleet data of any kind.
/// </para>
/// </summary>
public interface IBackgroundMonitoringSettingsService
{
    event EventHandler? BackgroundMonitoringEnabledChanged;

    /// <summary>Whether closing the window keeps the app monitoring in the background. Default: ON.</summary>
    bool BackgroundMonitoringEnabled { get; }

    /// <summary>
    /// True once the single first-close notice has been ATTEMPTED. Recorded before/atomically with the
    /// attempt, so a notice that Windows suppressed (notifications disabled or unavailable) still counts
    /// as spent: the product never nags, and the durable explanation lives in Settings instead.
    /// </summary>
    bool BackgroundNoticeShown { get; }

    void SetBackgroundMonitoringEnabled(bool enabled);

    /// <summary>
    /// Marks the notice as spent and reports whether THIS call is the one that claimed it. Exactly one
    /// caller ever gets true, so concurrent closes cannot produce two toasts.
    /// </summary>
    bool TryClaimBackgroundNotice();
}
