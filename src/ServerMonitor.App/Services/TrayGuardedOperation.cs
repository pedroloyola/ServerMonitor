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

    /// <summary>Performed instead when it does not. There is no third outcome and no silent one.</summary>
    void FallBackToExit();
}
