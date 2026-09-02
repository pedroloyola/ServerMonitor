namespace ServerMonitor.App.Shell.Tray;

/// <summary>
/// The internal lifecycle state of the tray affordance. Six states, one authority.
/// <para>
/// <c>Unavailable</c> is ordinal 0 so any uninitialised field or mock defaults to the safe value
/// (CV-4). <c>Releasing</c> and <c>Released</c> are absorbing: for every input <c>x</c>,
/// δ(Releasing, x) = Releasing, with the single internal exception of reaching <c>Released</c> once the
/// required compensation has been positively reconciled.
/// </para>
/// </summary>
internal enum TrayLifecycleState
{
    /// <summary>Never established in this session.</summary>
    Unavailable = 0,

    /// <summary>The shell confirmed both NIM_ADD and NIM_SETVERSION. The only producer is one branch.</summary>
    Available = 1,

    /// <summary>
    /// A bounded recovery episode is running. The previous proof is already invalid, so this is NOT
    /// Available; but an unauthenticated broadcast may not degrade the session either, so it is not Lost.
    /// </summary>
    Recovering = 2,

    /// <summary>
    /// Terminal for the affordance. Two legitimate causes only: an observed native failure that
    /// exhausted budget A, or expiry of the monotonic recovery deadline. Frequency suppression is
    /// neither, and never produces it.
    /// </summary>
    Lost = 3,

    /// <summary>Absorbing. No new Add may be emitted; in-flight work may only be reconciled.</summary>
    Releasing = 4,

    /// <summary>
    /// Reached only when every in-flight effect capable of leaving an affordance has been reconciled and
    /// the required compensation completed positively. It does NOT mean "Release returned".
    /// </summary>
    Released = 5
}
