using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Services;

/// <summary>The single first-close notice. One attempt, ever.</summary>
public interface IBackgroundNoticePresenter
{
    /// <summary>
    /// Shows the notice if it has never been attempted. Returns true when THIS call made the attempt.
    /// Never throws, never blocks, and never delays the caller's hide.
    /// </summary>
    bool TryShowOnce();
}

/// <summary>
/// Shows the approved one-time toast that explains the app is still running after the window closed
/// (M13 S2 §D.1).
/// <para>
/// <b>Why a toast.</b> It is the only ephemeral surface that stays visible after the Dashboard is gone —
/// an in-window teaching moment would disappear with the very window it is explaining.
/// </para>
/// <para>
/// <b>Spent on attempt, not on delivery.</b> The flag is claimed BEFORE the notification is handed to
/// Windows, so a notice suppressed by disabled or unavailable notifications still counts. The durable
/// explanation lives in Settings → Background; the product never nags.
/// </para>
/// <para>
/// <b>It never suppresses the transition and never blocks.</b> A failure here is logged and dropped: the
/// window is already hidden by the time this runs.
/// </para>
/// <para>
/// <b>It carries no fleet data</b> (Vigil C5): the strings are static resources, with no server name,
/// address, host or count anywhere in the title, body or payload.
/// </para>
/// </summary>
public sealed class BackgroundNoticePresenter(
    IBackgroundMonitoringSettingsService backgroundSettings,
    IUserNotificationService notificationService,
    ILocalizationService localizationService,
    IAppLifecycleController lifecycleController,
    ILogger<BackgroundNoticePresenter> logger) : IBackgroundNoticePresenter
{
    public bool TryShowOnce()
    {
        // A process that started headless never had a user-initiated FOREGROUND → BACKGROUND transition
        // to explain, so it must stay silent even if it later materializes and is closed... that close
        // IS a user transition, so only the automatic paths are excluded here, not this one.
        if (lifecycleController.IsExiting)
        {
            return false;
        }

        if (!backgroundSettings.TryClaimBackgroundNotice())
        {
            return false; // already spent: no nag, ever
        }

        try
        {
            notificationService.ShowBackgroundNotice(
                localizationService.GetString("BackgroundNoticeTitle"),
                localizationService.GetString("BackgroundNoticeBody"));
            logger.LogInformation("The one-time background notice was attempted.");
        }
        catch (Exception exception)
        {
            // The notice is a courtesy; Settings carries the durable explanation.
            logger.LogWarning(
                "The background notice could not be shown ({Type}); it stays spent.",
                exception.GetType().Name);
        }

        return true;
    }
}
