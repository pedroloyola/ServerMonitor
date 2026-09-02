using ServerMonitor.ActivationContract;

namespace ServerMonitor.App.Services;

/// <summary>Where a redirected activation came from. Classified, never inferred (M13 S2 §H.2).</summary>
public enum ActivationOrigin
{
    /// <summary>A person did something: opened the app, clicked the widget, clicked a notification.</summary>
    UserActivation,

    /// <summary>
    /// A second <c>--background</c> launch. It must NOT surface the UI of the running instance: a
    /// headless start is not a request to look at the Dashboard, and letting it restore would hand S4 a
    /// window that appears on its own.
    /// </summary>
    BackgroundLaunch
}

/// <summary>
/// One redirected activation in, EXACTLY ONE window restore out (M13-QA-10 defensive fix B), and only
/// when the activation actually asks for the UI (M13 S2 §H.2).
/// <para>
/// The redirect handler used to do both halves unconditionally: deliver the deep-link intent AND restore
/// the window. But a deep-link intent already ends in a restore — <c>App.ExecuteActivationIntent</c>
/// surfaces the window before it navigates — so a widget/protocol activation ran the restore path twice
/// (measured), which is two <c>Show</c>/<c>Activate</c>/foreground sequences on the UI thread for a single
/// user click and exactly the shape that produces focus flicker.
/// </para>
/// <para>
/// The rule is therefore: <b>deliver the intent always; restore here only when nothing else will, and
/// only when a person asked.</b> An activation with no intent (a plain second launch, a notification
/// click) has no intent path to restore it, so this class does it — unless it is a background launch,
/// which asks for no UI at all. An activation carrying an intent is restored by the intent's own
/// execution, so this class stays out of the way — including when the shell is not ready yet, because
/// then BOTH paths no-op and the buffered intent restores the window once, when the router drains it
/// (§M-1). And once the process is exiting, nothing is served at all (EXIT WINS).
/// </para>
/// This is deliberately a plain class over callbacks rather than a static helper: it is the seam that
/// lets the "one restore per logical activation" invariant be counted in a test, without a XAML runtime.
/// </summary>
public sealed class ActivationDispatch
{
    private readonly Action<ActivationIntent?> _deliverIntent;
    private readonly Action _restoreWindow;
    private readonly Func<bool> _isExiting;

    public ActivationDispatch(
        Action<ActivationIntent?> deliverIntent,
        Action restoreWindow,
        Func<bool>? isExiting = null)
    {
        _deliverIntent = deliverIntent ?? throw new ArgumentNullException(nameof(deliverIntent));
        _restoreWindow = restoreWindow ?? throw new ArgumentNullException(nameof(restoreWindow));
        _isExiting = isExiting ?? (static () => false);
    }

    /// <summary>
    /// Handles one redirected activation. <paramref name="intent"/> is null for an activation that carries
    /// no deep link. Order is preserved from the original handler: the intent is delivered first, so a
    /// newer activation can never be overtaken by the restore it triggers.
    /// </summary>
    public void Dispatch(ActivationIntent? intent, ActivationOrigin origin = ActivationOrigin.UserActivation)
    {
        // EXIT WINS: once the process has committed to exiting nothing is served — no intent execution,
        // no materialization, no restore. The activation is discarded and the true exit completes. The
        // check is first so a discarded activation cannot even reach the intent hand-off.
        if (_isExiting())
        {
            return;
        }

        _deliverIntent(intent);

        if (intent is null && origin == ActivationOrigin.UserActivation)
        {
            _restoreWindow();
        }
    }
}
