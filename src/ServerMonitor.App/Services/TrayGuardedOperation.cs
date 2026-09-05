namespace ServerMonitor.App.Services;

/// <summary>
/// The operations that may only be performed while the tray affordance holds — named as VALUES.
/// </summary>
/// <remarks>
/// <b>An enum, and that is the entire point.</b> Five times in this slice the same defect came back in a
/// new shape: a fabricable token, an implementable channel, a readable property, a returned bool, and
/// finally an arbitrary <c>Action</c> — which a caller fills with its own code
/// (<c>EnterBackground(() =&gt; permission = true)</c>), so the authorisation is captured and replayed
/// later. Each fix removed one way of OBTAINING the right and left the next, because the caller kept
/// running its own code inside the authorisation.
/// <para>
/// A value cannot capture anything. The caller names WHICH operation it wants; the machine owns the
/// concrete operation and invokes it itself, under the same lock that decides it is allowed.
/// </para>
/// </remarks>
public enum TrayGuardedOperation
{
    /// <summary>Hide the window and continue monitoring in the background.</summary>
    EnterBackground = 0,

    /// <summary>
    /// Hide the window on minimize — which is the SAME window mechanics as background entry, and was the
    /// second door.
    /// </summary>
    /// <remarks>
    /// <c>HideForMinimize</c> and <c>HideToBackground</c> differed by a log line: both set
    /// <c>IsShownInSwitchers = false</c> and called <c>Hide()</c>. Guarding one and leaving the other on
    /// the general contract closed the door by name and left it open by effect, and the minimize caller
    /// was guarded only by "the service is started" — so a user who minimized after a failed registration
    /// got a hidden window with no tray icon, which is the A12 zombie by a third route.
    /// </remarks>
    HideForMinimize = 1,
}

/// <summary>
/// The concrete operations the state machine OWNS and invokes. Registered once, at composition time.
/// </summary>
/// <remarks>
/// The caller never supplies one of these and never holds one: it is handed to the machine by the
/// composition root, single-assignment, exactly like <see cref="ITrayLossConsumer"/>. Implemented
/// EXPLICITLY by the owner so it stays off that class's public surface.
/// <para>
/// Both outcomes live here because the machine decides between them under its lock. The caller therefore
/// learns nothing from which one ran — and, more importantly, could do nothing with the knowledge: the
/// window-hiding operation is not reachable from it at all. That is what makes this the last ring rather
/// than the sixth of a series. Every previous correction removed the TICKET and left the ACTION callable.
/// </para>
/// </remarks>
public interface ITrayGuardedOperations
{
    /// <summary>Performed only while the affordance holds and the session has not degraded.</summary>
    void EnterBackground();

    /// <summary>Hides on minimize, under the same guard and for the same reason.</summary>
    void HideForMinimize();

    /// <summary>
    /// Performed instead when the guard refuses. There is no third outcome and no silent one — but WHAT
    /// the refusal does depends on the operation, which is why it takes one.
    /// </summary>
    /// <remarks>
    /// Refusing a background entry has to close the window, because the user asked for the window to go
    /// away and it did not. Refusing a MINIMIZE must not: the user asked to minimize, and quitting the
    /// application because the tray is unavailable would be a far worse answer than leaving the window
    /// where it is. One refusal shape for both would have got one of them wrong.
    /// </remarks>
    void Refuse(TrayGuardedOperation operation);
}
