namespace ServerMonitor.App.Services;

/// <summary>
/// Whether the process actually holds a usable notification registration (M13-QA-12).
/// <para>
/// It exists because "best effort" was being read as "pretend the registration succeeded". A failed
/// registration used to be indistinguishable from a successful one at every call site, so the single
/// first-close notice was spent against a platform that could not deliver anything. The state is
/// reported, never inferred, and only <see cref="Registered"/> means the service can be used.
/// </para>
/// </summary>
public enum NotificationRegistrationState
{
    /// <summary>
    /// Startup has not run, it failed, or the registration was released again. The service is usable for
    /// nothing; it must not be asked to carry a one-shot opportunity.
    /// </summary>
    NotRegistered,

    /// <summary>
    /// The platform accepted the registration and the activation handler is attached. Delivery of any
    /// individual notification remains Windows' decision.
    /// </summary>
    Registered,

    /// <summary>
    /// The platform cannot be used on this system at all — unsupported, or its required asset is
    /// missing — so registration was never attempted. Kept apart from
    /// <see cref="NotRegistered"/> for diagnosis only: both mean "not usable".
    /// </summary>
    Unavailable
}

/// <summary>
/// What became of the single first-close notice opportunity (M13-QA-12).
/// <para>
/// The approved meaning of the persisted <c>BackgroundNoticeShown</c> marker is NOT "some code called the
/// notification API". It is "the one-time warning opportunity was legitimately exercised through an
/// OPERATIONALLY REGISTERED notification service". This type is what carries that distinction back to the
/// caller, so the marker is never spent by a call that went nowhere.
/// </para>
/// </summary>
public enum BackgroundNoticeAttempt
{
    /// <summary>
    /// Nothing was handed to Windows, because the service is not operationally registered. The
    /// opportunity was NOT exercised and must remain available for a later session.
    /// </summary>
    NotAttempted,

    /// <summary>
    /// The notice was handed to a registered platform. Whether Windows displays it — the user may have
    /// turned notifications off, the shell may drop it — is deliberately NOT checked: no delivery
    /// acknowledgement is required, because Windows does not guarantee one. The opportunity is spent.
    /// </summary>
    ExercisedThroughRegisteredService
}

/// <summary>Platform boundary that displays an already-evaluated local notification.</summary>
public interface IUserNotificationService
{
    /// <summary>
    /// The reported registration state. Implementations that register nothing say so; none may report
    /// <see cref="NotificationRegistrationState.Registered"/> without an accepted registration.
    /// </summary>
    NotificationRegistrationState RegistrationState => NotificationRegistrationState.NotRegistered;
    /// <summary>Synchronously rejects new delivery/activation work before hosted-service drain.</summary>
    void BeginShutdown() { }

    Task ShowAsync(UserNotification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows the single first-close background notice (M13 S2 §D.1). Separate from
    /// <see cref="ShowAsync"/> because it is not a health notification: it carries the background
    /// activation contract instead of the health one, is short-lived, and does not persist in the
    /// Notification Centre.
    /// <para>
    /// Returns what became of the single opportunity (M13-QA-12): the caller may spend the persisted
    /// marker only for <see cref="BackgroundNoticeAttempt.ExercisedThroughRegisteredService"/>.
    /// </para>
    /// </summary>
    BackgroundNoticeAttempt ShowBackgroundNotice(string title, string body) =>
        BackgroundNoticeAttempt.NotAttempted;

    /// <summary>
    /// Shows the fail-safe exit notice (CV-17). Separate for the same reason as the background notice:
    /// it carries its own closed activation pair, and it is even shorter-lived.
    /// <para>
    /// <b>Fire and forget.</b> It returns nothing and is never awaited, because it is called from the
    /// committed exit path: the exit does not depend on it, does not wait for delivery, and does not
    /// learn whether the user ever saw it.
    /// </para>
    /// </summary>
    void ShowFailSafeExitNotice(string title, string body) { }
}
