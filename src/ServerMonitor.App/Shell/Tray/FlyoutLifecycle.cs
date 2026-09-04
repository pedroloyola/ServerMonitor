using System.Drawing;

namespace ServerMonitor.App.Shell.Tray;

/// <summary>
/// The platform side of a tray flyout, as a seam. Everything here needs a XAML runtime or a Win32 hook;
/// nothing here decides anything.
/// </summary>
internal interface IFlyoutSurface
{
    /// <summary>Whether the XAML root can host a flyout right now — a real condition, not a delay.</summary>
    bool IsPresentable { get; }

    void MoveTo(Point anchor);

    void Activate();

    /// <summary>
    /// Installs the dismissal watch. <b>False means refused</b>, and a refusal is fatal to the request:
    /// the menu must not be shown when nothing can ever tell us it was dismissed.
    /// </summary>
    bool TryInstallDismissalWatch();

    /// <summary>Removes the watch. Idempotent, and must not throw.</summary>
    void RemoveDismissalWatch();

    /// <summary>Shows the menu. May throw.</summary>
    void PresentMenu();

    /// <summary>Hides the menu. <b>False means it did not happen</b> — there was none, or it failed.</summary>
    bool TryHideMenu();

    /// <summary>Hides the auxiliary window. Must not throw.</summary>
    void HideWindow();

    /// <summary>
    /// Identifies whoever owns the foreground right now, so a change across an interval can be detected
    /// without waiting for an event that may already have passed.
    /// </summary>
    nint CaptureForeground();
}

/// <summary>
/// The lifecycle of ONE flyout request, from asked-for to released — and the guarantee that the slot is
/// released exactly once, on every path.
/// </summary>
/// <remarks>
/// <para>
/// It lives apart from <see cref="TrayFlyoutWindow"/> because the policy is decidable and the presentation
/// is not: <c>XamlRoot</c>, <c>MenuFlyout.ShowAt</c> and <c>SetWinEventHook</c> need a desktop, but "what
/// must happen when the watch is refused" does not. M13-QA-11 was two liveness failures, and both were in
/// the policy rather than in the drawing.
/// </para>
/// <para>
/// <b>The rule this type exists to keep:</b> every terminal path calls <c>release</c> exactly once. The
/// original defect was a release that hung off a single event which, in three of four measured states,
/// never arrived. Replacing it with a different single event would be the same defect one layer along —
/// so the refusal of the watch, a failed hide, and disposal are all terminals here, not special cases.
/// </para>
/// </remarks>
internal sealed class FlyoutLifecycle(IFlyoutSurface surface, Action release)
{
    private readonly IFlyoutSurface _surface = surface ?? throw new ArgumentNullException(nameof(surface));
    private readonly Action _release = release ?? throw new ArgumentNullException(nameof(release));

    private bool _awaitingReadiness;
    private bool _presented;
    private bool _released;
    private bool _disposed;

    internal bool IsPresented => _presented;

    internal bool IsAwaitingReadiness => _awaitingReadiness;

    /// <summary>A request has arrived. Presents now, or waits for readiness, or terminates.</summary>
    internal void Show(Point anchor)
    {
        if (_disposed)
        {
            Terminate();
            return;
        }

        _released = false;
        _presented = false;

        _surface.MoveTo(anchor);
        _surface.Activate();

        if (_surface.IsPresentable)
        {
            Present();
            return;
        }


        // READINESS, NOT RETRY. The click is kept and shown when the tree can host it.
        _awaitingReadiness = true;
    }

    /// <summary>The XAML tree finished loading. Presents the request that was waiting for it.</summary>
    internal void OnSurfaceReady()
    {
        if (!_awaitingReadiness)
        {
            return;
        }

        _awaitingReadiness = false;

        if (_disposed || !_surface.IsPresentable)
        {
            // The tree arrived unusable. The click is lost; the SLOT is not.
            Terminate();
            return;
        }

        Present();
    }

    /// <summary>The foreground moved to something that is not us: that is the dismissal.</summary>
    internal void OnForeignForeground()
    {
        if (!_presented)
        {
            return;
        }

        if (!_surface.TryHideMenu())
        {
            // The hide did not happen, so no Closed is coming. Terminating here is the whole point: the
            // slot must not depend on a notification that the platform has already declined to send.
            Terminate();
        }
    }

    /// <summary>The menu reported that it closed.</summary>
    internal void OnMenuClosed() => Terminate();

    /// <summary>The window is going away.</summary>
    internal void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _surface.TryHideMenu();
        Terminate();
    }

    private void Present()
    {
        // The baseline is taken BEFORE the menu exists, because the watch only reports FUTURE changes and
        // there is an interval between showing and installing it. A change that lands in that interval
        // produces no callback ever, and the slot would be held until some later, unrelated change.
        var foregroundAtOpen = _surface.CaptureForeground();

        try
        {
            _surface.PresentMenu();
            _presented = true;
        }
        catch (Exception)
        {
            Terminate();
            throw;
        }

        // The watch goes in AFTER the menu is up, and its refusal is still fatal. Installing it first
        // caught the foreground churn the popup itself causes and dismissed the menu on sight -- measured.
        // Zero from SetWinEventHook is a documented failure, and a menu nothing can dismiss would hold the
        // slot for the session, so the request is torn down rather than left open.
        if (!_surface.TryInstallDismissalWatch())
        {
            _surface.TryHideMenu();
            Terminate();
            return;
        }

        // AND THEN CLOSE THE BLIND WINDOW. Comparing the level now against the baseline catches any change
        // that happened while nothing was listening. It is a comparison against THIS menu's own starting
        // point, not "is the foreground foreign" -- in the states this defect lives in, the foreground is
        // ALREADY foreign when the menu legitimately opens, so a bare level check would dismiss every one
        // of them on sight.
        if (_surface.CaptureForeground() != foregroundAtOpen)
        {
            OnForeignForeground();
        }
    }

    /// <summary>The single exit. Idempotent, so no path can release the slot twice.</summary>
    private void Terminate()
    {
        _awaitingReadiness = false;
        _presented = false;

        _surface.RemoveDismissalWatch();
        _surface.HideWindow();

        if (_released)
        {
            return;
        }

        _released = true;
        _release();
    }
}
