namespace ServerMonitor.App.Services;

/// <summary>
/// Whether a true-exit affordance is positively established. This is the S2-T contract, consumed by S2.
/// <para>
/// The three states are the ones the split decision fixed. There is deliberately no "probably" and no
/// "we called Start and it returned": <see cref="Available"/> may only ever be reported after a shell
/// registration reported REAL success through the native boundary S2-T owns.
/// </para>
/// </summary>
public enum TrayAffordanceState
{
    /// <summary>
    /// No affordance is established. The starting state, and where a registration that never succeeded
    /// stays. BACKGROUND is not legitimate here.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The shell confirmed it holds the icon. The ONLY state in which the window may be hidden.
    /// </summary>
    Available,

    /// <summary>
    /// A bounded recovery episode is running: the previous proof is already invalid, so this is NOT
    /// Available, but an unauthenticated <c>TaskbarCreated</c> broadcast must not degrade the session
    /// either, so it is not <see cref="Lost"/>. S2 HOLDS here — it neither degrades nor treats the tray
    /// as usable — for at most the bounded recovery deadline (M13 S2-T).
    /// </summary>
    Recovering,

    /// <summary>
    /// An affordance that WAS established is gone — Explorer restarted and re-registration failed within
    /// its budget. Treated exactly like <see cref="Unavailable"/> for the close semantics, but reported
    /// separately because it can happen mid-session while the window is already hidden.
    /// </summary>
    Lost
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

/// <summary>
/// The placeholder S2 programs against until S2-T lands (split decision: "programa contra a forma
/// conceptual").
/// <para>
/// It reports <see cref="TrayAffordanceState.Unavailable"/> and nothing else, on purpose: S2 has no way
/// to establish an affordance and must not pretend otherwise. That makes an interim build degrade to a
/// foreground session with true-exit semantics — the fail-closed outcome the contract demands — rather
/// than repeat the fiction this seam exists to remove. It owns no window, registers no icon and calls no
/// shell API: there is no duplicated ownership here for S2-T to collide with.
/// </para>
/// </summary>
public sealed class PendingTrayAffordanceSource : ITrayAffordanceSource
{
    /// <summary>Never raised: the state never changes until S2-T supplies a real source.</summary>
    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public TrayAffordanceState State => TrayAffordanceState.Unavailable;
}
