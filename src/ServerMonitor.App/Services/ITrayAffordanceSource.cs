namespace ServerMonitor.App.Services;

/// <summary>
/// Whether a true-exit affordance is positively established. This is the S2-T contract, consumed by S2.
/// <para>
/// <b>Four states</b>, closed. The split decision fixed three; <see cref="Recovering"/> was added when
/// S2-T landed, because a bounded revalidation window is neither of its neighbours. There is deliberately
/// no "probably" and no "we called Start and it returned": <see cref="Available"/> may only ever be
/// reported after a shell registration reported REAL success through the native boundary S2-T owns.
/// </para>
/// <para>
/// <b>The values are explicit and NEVER serialized.</b> Nothing outside this assembly names this type,
/// nothing casts it to <see cref="int"/>, nothing compares it with an order, and no payload, file or
/// registry value carries it — so <see cref="Recovering"/> could be inserted in the middle without
/// breaking anything. The numbers are written down anyway, because a guarantee that rests on nobody ever
/// doing one of those things should be a guarantee somebody can read.
/// </para>
/// </summary>
public enum TrayAffordanceState
{
    /// <summary>
    /// No affordance is established. The starting state, and where a registration that never succeeded
    /// stays. BACKGROUND is not legitimate here.
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// The shell confirmed it holds the icon. The ONLY state in which the window may be hidden.
    /// </summary>
    Available = 1,

    /// <summary>
    /// A bounded recovery episode is running: the previous proof is already invalid, so this is NOT
    /// Available, but an unauthenticated <c>TaskbarCreated</c> broadcast must not degrade the session
    /// either, so it is not <see cref="Lost"/>. S2 HOLDS here — it neither degrades nor treats the tray
    /// as usable — for at most the bounded recovery deadline (M13 S2-T).
    /// </summary>
    Recovering = 2,

    /// <summary>
    /// An affordance that WAS established is gone — Explorer restarted and re-registration failed within
    /// its budget. Treated exactly like <see cref="Unavailable"/> for the close semantics, but reported
    /// separately because it can happen mid-session while the window is already hidden.
    /// </summary>
    Lost = 3
}

/// <summary>
/// The seam through which S2 learns whether the tray affordance exists (M13 S2-T split, 2026-09-02).
/// <para>
/// <b>S2 does not implement this.</b> Physical tray reliability — the owned Win32 window, the
/// <c>Shell_NotifyIcon</c> registration and its observed result, callback routing, flyout hosting,
/// <c>TaskbarCreated</c> re-registration and the bounded retry machine — belongs to S2-T. S2 owns only
/// what the states MEAN for the lifecycle, and consumes them.
/// </para>
/// <para>
/// <b>What S2 may never do</b>, because none of it proves the shell currently holds the icon: infer
/// availability from a <c>Start()</c> that returned, from an internal <c>_started</c> flag, from a tray
/// object existing, or from a <c>NotifyIconSettings</c> registry entry. The previous implementation did
/// exactly the first two, which is why a silent <c>NIM_ADD</c> failure could leave a headless process
/// monitoring with no way out.
/// </para>
/// </summary>
public interface ITrayAffordanceSource
{
    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// Registers the ONE consumer whose handling of a loss is authoritative. Single assignment: a second
    /// registration throws, so this can never become a second multicast list.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="StateChanged"/> on purpose. A loss has exactly one consumer and ending the
    /// process is its consequence, so it may not travel with the observers: while it did, an observer that
    /// threw after the loss was already handled still forced a fail-safe exit. Single assignment also stops
    /// the inverse abuse — a late caller registering ITSELF as the authoritative consumer and absorbing
    /// every loss silently, which would suppress the fail-safe instead of triggering it.
    /// </remarks>
    void SetLossConsumer(ITrayLossConsumer consumer);

    /// <summary>The current, positively established state. Never optimistic.</summary>
    /// <remarks>
    /// <b>For observing, never for authorising.</b> A caller that reads this and acts on it afterwards has
    /// a value that was true once; between the read and the act the affordance can be lost, and the act
    /// goes ahead anyway. To ACT on the affordance, use <see cref="EnterBackground"/>, which is the only
    /// path that authorises anything and which revalidates for itself.
    /// </remarks>
    TrayAffordanceState State { get; }

    /// <summary>
    /// Enters background — ATOMICALLY with establishing that it is allowed.
    /// </summary>
    /// <remarks>
    /// <b>Why a delegate and not a boolean.</b> The permission used to be read as a <c>bool</c> and acted
    /// on a moment later, and a probe that invalidated the affordance in that gap still hid the window:
    /// the process was left alive, invisible and with no way out, which is the A12 defect reached by a
    /// third door. A permission that can be held is a capability that circulates, and a capability that
    /// circulates is one that can be fabricated — the same correction this slice already made for the
    /// episode token and for the effect channel.
    /// <para>
    /// So the RIGHT crosses the boundary, not the answer: the caller hands over what it wants done, and it
    /// is performed under the same lock that decided it was permitted. There is no interval to lose.
    /// </para>
    /// <para>
    /// <b>And it returns NOTHING.</b> Returning a boolean was the same capability in a new shape: called
    /// with an empty action it hands back a bare "you are permitted", which the caller keeps and acts on
    /// later — precisely the defect the delegate was supposed to remove. A caller that needs to know what
    /// happened learns it from inside its own action, where the answer is a record of a completed act and
    /// not a right to perform one.
    /// </para>
    /// </remarks>
    /// <param name="enterBackground">Run only if the affordance is established. Must not block.</param>
    void EnterBackground(Action enterBackground);
}
