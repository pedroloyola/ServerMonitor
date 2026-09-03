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

    /// <summary>The current, positively established state. Never optimistic.</summary>
    TrayAffordanceState State { get; }
}
