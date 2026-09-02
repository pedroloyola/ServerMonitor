using ServerMonitor.ActivationContract;

namespace ServerMonitor.App.Services;

/// <summary>
/// One redirected activation in, EXACTLY ONE window restore out (M13-QA-10 defensive fix B).
/// <para>
/// The redirect handler used to do both halves unconditionally: deliver the deep-link intent AND restore
/// the window. But a deep-link intent already ends in a restore — <c>App.ExecuteActivationIntent</c>
/// surfaces the window before it navigates — so a widget/protocol activation ran the restore path twice
/// (measured), which is two <c>Show</c>/<c>Activate</c>/foreground sequences on the UI thread for a single
/// user click and exactly the shape that produces focus flicker.
/// </para>
/// <para>
/// The rule is therefore: <b>deliver the intent always; restore here only when nothing else will.</b> An
/// activation with no intent (a plain second launch, a notification click) has no intent path to restore
/// it, so this class does it. An activation carrying an intent is restored by the intent's own execution,
/// so this class stays out of the way — including when the shell is not ready yet, because then BOTH
/// paths no-op and the buffered intent restores the window once, when the router drains it (§M-1).
/// </para>
/// This is deliberately a plain class over two callbacks rather than a static helper: it is the seam that
/// lets the "one restore per logical activation" invariant be counted in a test, without a XAML runtime.
/// </summary>
public sealed class ActivationDispatch
{
    private readonly Action<ActivationIntent?> _deliverIntent;
    private readonly Action _restoreWindow;

    public ActivationDispatch(Action<ActivationIntent?> deliverIntent, Action restoreWindow)
    {
        _deliverIntent = deliverIntent ?? throw new ArgumentNullException(nameof(deliverIntent));
        _restoreWindow = restoreWindow ?? throw new ArgumentNullException(nameof(restoreWindow));
    }

    /// <summary>
    /// Handles one redirected activation. <paramref name="intent"/> is null for an activation that carries
    /// no deep link. Order is preserved from the original handler: the intent is delivered first, so a
    /// newer activation can never be overtaken by the restore it triggers.
    /// </summary>
    public void Dispatch(ActivationIntent? intent)
    {
        _deliverIntent(intent);

        if (intent is null)
        {
            _restoreWindow();
        }
    }
}
