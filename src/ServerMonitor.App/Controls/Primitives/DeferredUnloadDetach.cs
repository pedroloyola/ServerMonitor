namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.12 C-L1: the stale-Unloaded rule of <see cref="SaLiveTree"/> (UI.11C, measured: after a SaThemeRefresh remount WinUI can
/// raise the new Loaded BEFORE the stale Unloaded) for a control that detaches from its view model on Unloaded. The detach is
/// deferred one dispatcher turn and skipped when the element is live again, so a remount never leaves the control detached
/// with nothing to re-attach it. Pure and WinUI-free; the liveness probe, the post and the detach are injected.
/// </summary>
internal sealed class DeferredUnloadDetach(Func<bool> isLive, Func<Action, bool> post, Action detach)
{
    private readonly Func<bool> _isLive = isLive ?? throw new ArgumentNullException(nameof(isLive));
    private readonly Func<Action, bool> _post = post ?? throw new ArgumentNullException(nameof(post));
    private readonly Action _detach = detach ?? throw new ArgumentNullException(nameof(detach));

    public void OnUnloaded()
    {
        // A queue that is shutting down cannot run the check: the window is going away, so detach now (the old behaviour).
        if (!_post(DetachUnlessLive))
        {
            _detach();
        }
    }

    private void DetachUnlessLive()
    {
        if (!_isLive())
        {
            _detach();
        }
    }
}
