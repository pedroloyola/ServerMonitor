namespace ServerMonitor.App.Shell.Tray;

/// <summary>
/// CV-9: exactly one flyout may be open at a time, and a second request while one is open produces
/// <b>nothing at all</b>.
/// <para>
/// The threat is not a user double-clicking. <c>WM_CONTEXTMENU</c> is a message any local process can
/// send to a window it can find, and the callback message id is guessable by design — so the reentrant
/// request is assumed hostile and repeatable. What it must not be able to do is open a second flyout,
/// move the open one to coordinates of its choosing, disturb a recovery episode, or make the auxiliary
/// window visible.
/// </para>
/// <para>
/// It is a separate type, and not a <c>bool</c> inside the adapter, for one reason: as a type it can be
/// proven. A flag guarding a call into XAML can only be exercised with a desktop, and the whole point of
/// the S2-T split is that the decidable parts are decided in tests.
/// </para>
/// </summary>
internal sealed class FlyoutReentrancyGate
{
    private readonly object _sync = new();
    private bool _open;

    /// <summary>Whether a flyout is currently open.</summary>
    internal bool IsOpen
    {
        get { lock (_sync) { return _open; } }
    }

    /// <summary>
    /// Claims the single flyout slot.
    /// </summary>
    /// <returns>
    /// True exactly once per open/close cycle. A false return means the caller must do NOTHING — not
    /// reposition, not re-show, not close the existing one and reopen. Returning false is the whole
    /// behaviour, so any "helpful" fallback at the call site defeats it.
    /// </returns>
    internal bool TryOpen()
    {
        lock (_sync)
        {
            if (_open)
            {
                return false;
            }

            _open = true;
            return true;
        }
    }

    /// <summary>
    /// Releases the slot. Idempotent, because the close notification can arrive more than once (a
    /// dismissal and a programmatic close race), and a gate that could be released twice would let a
    /// pending hostile request through on the second one.
    /// </summary>
    internal void Close()
    {
        lock (_sync)
        {
            _open = false;
        }
    }
}
