namespace ServerMonitor.App.Views;

/// <summary>
/// UI.8 Atlas c1 A-3: the lifecycle of the Compact entry-focus wait, out of the window so it is tested without WinUI.
/// <see cref="Begin"/> tries once; when the target is not laid out yet it subscribes ONE layout handler and retries on
/// each pass (the pass count goes to <see cref="CompactEntryFocus.Decide"/>, which falls back after
/// <see cref="CompactEntryFocus.MaxLayoutPasses"/>). It unsubscribes after success, after the fallback, and on
/// <see cref="End"/> (leaving Compact, closing the window). A pass that arrives after <see cref="End"/> - a callback the
/// layout system had already captured - never moves focus.
/// Beacon c2 B-6 (measured): a settled layout raises no further LayoutUpdated (5 passes, then silence, with row 1 never
/// realized), so the fallback was never reached. Every waiting outcome therefore REQUESTS the next layout pass: the wait
/// always ends - on a target or on the bounded fallback - without a timer.
/// </summary>
internal sealed class CompactEntryFocusWait
{
    private readonly Action<EventHandler<object>> _subscribe;
    private readonly Action<EventHandler<object>> _unsubscribe;
    private readonly Func<int, bool> _tryFocus;
    private readonly Action _requestLayoutPass;
    private readonly EventHandler<object> _onLayoutUpdated;

    /// <param name="subscribe">Adds the handler to the compact body's LayoutUpdated.</param>
    /// <param name="unsubscribe">Removes it.</param>
    /// <param name="tryFocus">Given the layout passes waited, places focus and returns true, or returns false to wait.</param>
    /// <param name="requestLayoutPass">Schedules one more layout pass of the compact body (e.g. InvalidateMeasure).</param>
    public CompactEntryFocusWait(
        Action<EventHandler<object>> subscribe,
        Action<EventHandler<object>> unsubscribe,
        Func<int, bool> tryFocus,
        Action requestLayoutPass)
    {
        _subscribe = subscribe ?? throw new ArgumentNullException(nameof(subscribe));
        _unsubscribe = unsubscribe ?? throw new ArgumentNullException(nameof(unsubscribe));
        _tryFocus = tryFocus ?? throw new ArgumentNullException(nameof(tryFocus));
        _requestLayoutPass = requestLayoutPass ?? throw new ArgumentNullException(nameof(requestLayoutPass));
        _onLayoutUpdated = OnLayoutUpdated;
    }

    /// <summary>True while a layout handler is subscribed.</summary>
    public bool IsWaiting { get; private set; }

    public int LayoutPasses { get; private set; }

    public void Begin()
    {
        End();
        LayoutPasses = 0;
        if (_tryFocus(LayoutPasses))
        {
            return;
        }

        IsWaiting = true;
        _subscribe(_onLayoutUpdated);
        _requestLayoutPass();
    }

    public void End()
    {
        if (!IsWaiting)
        {
            return;
        }

        IsWaiting = false;
        _unsubscribe(_onLayoutUpdated);
    }

    private void OnLayoutUpdated(object? sender, object e)
    {
        if (!IsWaiting)
        {
            return;
        }

        LayoutPasses++;
        if (_tryFocus(LayoutPasses))
        {
            End();
            return;
        }

        _requestLayoutPass();
    }
}
