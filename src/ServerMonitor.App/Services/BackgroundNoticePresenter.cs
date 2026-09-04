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
/// <b>Spent on a LEGITIMATE attempt, not on delivery</b> (M13-QA-12). The marker does not mean "some code
/// called the notification API": it means the one-time opportunity was exercised through an operationally
/// registered service. So a notice Windows chose not to display still counts — delivery is best effort and
/// no acknowledgement is sought — while a notice that was never handed to anything, because the
/// registration failed, does NOT: the flag stays unclaimed and the next session can still explain itself.
/// The durable explanation lives in Settings → Background; the product never nags.
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
    ILogger<BackgroundNoticePresenter> logger,
    INotificationRegistrationEvidence? evidence = null) : IBackgroundNoticePresenter
{
    private readonly INotificationRegistrationEvidence _evidence =
        evidence ?? NullNotificationRegistrationEvidence.Instance;

    /// <summary>
    /// Serializes the read-attempt-claim sequence. The claim used to be a single atomic operation that
    /// also decided whether to show, which is what made "spend it whatever happens" the only expressible
    /// answer. Splitting the decision costs this lock and buys the invariant the marker is supposed to
    /// carry.
    /// </summary>
    private readonly object _sync = new();

    public bool TryShowOnce()
    {
        lock (_sync)
        {
            // A process that started headless never had a user-initiated FOREGROUND → BACKGROUND
            // transition to explain, so it must stay silent even if it later materializes and is
            // closed... that close IS a user transition, so only the automatic paths are excluded here.
            if (lifecycleController.IsExiting)
            {
                return false;
            }

            if (backgroundSettings.BackgroundNoticeShown)
            {
                return false; // already spent: no nag, ever
            }

            var attempt = Attempt();
            if (attempt != BackgroundNoticeAttempt.ExercisedThroughRegisteredService)
            {
                // M13-QA-12. Nothing reached Windows, so nothing was explained to anyone: spending the
                // one-shot marker here would silently cost the user the only warning they ever get. The
                // transition to background is NOT affected — this method never gates it — and there is
                // no modal, no retry and no second surface. Just the evidence.
                logger.LogError(
                    "The one-time background notice was NOT delivered to the platform ({State}); the "
                    + "single warning opportunity stays available.",
                    notificationService.RegistrationState);
                RecordOutcome(attempt, claimed: false);
                return false;
            }

            logger.LogInformation("The one-time background notice was attempted through a registered service.");
            var claimed = backgroundSettings.TryClaimBackgroundNotice();
            RecordOutcome(attempt, claimed);
            return claimed;
        }
    }

    /// <summary>
    /// The notice half of the M13-QA-12 record. This method is reached only from the background-entry
    /// path, AFTER the window was hidden and the lifecycle moved to BACKGROUND, so its presence in the
    /// file is itself the evidence that the transition was not blocked by any of this.
    /// </summary>
    private void RecordOutcome(BackgroundNoticeAttempt attempt, bool claimed) =>
        _evidence.Append(
            $"noticeTimestampUtc={DateTimeOffset.UtcNow:O}"
            + $" registrationState={notificationService.RegistrationState}"
            + $" attempt={attempt}"
            + $" markerConsumedByThisAttempt={claimed}"
            + $" markerNowSpent={backgroundSettings.BackgroundNoticeShown}"
            + " reachedFromBackgroundEntry=true (the hide and the BACKGROUND transition already happened)");

    /// <summary>
    /// One attempt, and what it was worth. A throwing implementation reports nothing, so the registration
    /// state answers instead: a service that IS registered has had its opportunity exercised even if the
    /// call then failed, and one that is not has not.
    /// </summary>
    private BackgroundNoticeAttempt Attempt()
    {
        try
        {
            return notificationService.ShowBackgroundNotice(
                localizationService.GetString("BackgroundNoticeTitle"),
                localizationService.GetString("BackgroundNoticeBody"));
        }
        catch (Exception exception)
        {
            // The notice is a courtesy; Settings carries the durable explanation.
            logger.LogWarning(
                "The background notice could not be shown ({Type}).",
                exception.GetType().Name);

            return notificationService.RegistrationState == NotificationRegistrationState.Registered
                ? BackgroundNoticeAttempt.ExercisedThroughRegisteredService
                : BackgroundNoticeAttempt.NotAttempted;
        }
    }
}
