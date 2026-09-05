using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Services;

/// <summary>
/// CV-17: the informational notice raised when the app closes itself because the notification-area icon
/// could not be positively removed.
/// <para>
/// <b>Only when the fail-safe exit WON the CAS (Prism, binding).</b> It is invoked from
/// <see cref="AppLifecycleController"/> on the branch that performed the transition to Exiting, and it
/// answers only to <see cref="ExitReason.TrayCleanupUnverified"/>. If the user had already asked to
/// quit — "Sair do ServerAlyzer", or closing the window with background monitoring off — and the
/// compensation then failed during that exit, there is NO notice: the outcome is the one they asked for,
/// and "open ServerAlyzer again to continue monitoring" would contradict their own action.
/// </para>
/// <para>
/// <b>Fire and forget.</b> Nothing here is awaited and nothing is returned to the exit path. A notice
/// that cannot be built or shown is logged and abandoned; the exit continues identically either way. The
/// process closing is the safe outcome, and the notice only explains it.
/// </para>
/// <para>
/// <b>What it does not say.</b> No server, host, address or fleet count — the payload has no field for
/// them (CV-18) and the copy has no place for them. No <c>Shell_NotifyIcon</c>, no <c>NIM_DELETE</c>, no
/// error code: the user is told the icon could not be safely restored and that reopening the app resumes
/// monitoring, which is everything they can act on.
/// </para>
/// </summary>
public sealed class FailSafeExitNotice(
    Func<IUserNotificationService> notificationService,
    ILocalizationService localizationService,
    ILogger<FailSafeExitNotice> logger)
{
    internal const string TitleResourceKey = "TrayFailSafeExitNotificationTitle";

    internal const string BodyResourceKey = "TrayFailSafeExitNotificationBody";

    private readonly Func<IUserNotificationService> _notificationService =
        notificationService ?? throw new ArgumentNullException(nameof(notificationService));

    private readonly ILocalizationService _localizationService =
        localizationService ?? throw new ArgumentNullException(nameof(localizationService));

    private readonly ILogger<FailSafeExitNotice> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private int _raised;

    /// <summary>Whether the notice has been raised. Diagnostic, and what the tests observe.</summary>
    public bool Raised => Volatile.Read(ref _raised) != 0;

    /// <summary>
    /// Called from the exit path, and ONLY on the branch that won the transition to Exiting.
    /// </summary>
    /// <param name="reason">Why the exit was requested. Anything but the fail-safe reason is ignored.</param>
    public void OnExitCommitted(ExitReason reason)
    {
        if (reason != ExitReason.TrayCleanupUnverified)
        {
            return;
        }

        // One shot. The fail-safe path is already RunOnce upstream; this makes a second notice
        // unrepresentable rather than merely unlikely.
        if (Interlocked.Exchange(ref _raised, 1) != 0)
        {
            return;
        }

        try
        {
            _notificationService().ShowFailSafeExitNotice(
                _localizationService.GetString(TitleResourceKey),
                _localizationService.GetString(BodyResourceKey));
        }
        catch (Exception exception)
        {
            // Swallowed deliberately, and this is the guarantee: the exit must not be delayed, altered or
            // prevented by anything that happens here. Resolving the service, reading the strings and
            // showing the toast are all inside the try for that reason.
            _logger.LogWarning(exception, "The fail-safe exit notice could not be raised; exiting anyway.");
        }
    }
}
