namespace ServerMonitor.App.Services;

/// <summary>
/// The ONE consumer whose handling of a lost tray affordance is what keeps the process honest, and the
/// reason it is not an event.
/// </summary>
/// <remarks>
/// <para>
/// A loss is not an item of news. It is what degrades the session or ends the process, and it has exactly
/// one consumer, so it may not be delivered through the same multicast as the observers. While it was, the
/// machine could only see the state it had delivered and not WHO had failed: an observer that threw after
/// the loss had already been handled correctly still produced a fail-safe exit — a defective observer was
/// a quit button — and a consumer that never ran at all was indistinguishable from one that had.
/// </para>
/// <para>
/// So the critical boundary is direct and named, the confirmation is explicit, and only the failure or the
/// ABSENCE of that confirmation escalates. Observers keep the event, keep their isolation, and can no
/// longer end the process.
/// </para>
/// <para>
/// <b>This interface is a duty, not a permission.</b> It carries no return value and grants its holder
/// nothing: what circulates is the obligation to act on a loss, in the opposite direction to a capability.
/// It is implemented EXPLICITLY by the consumer so that it stays off that class's public surface and can
/// only be invoked by whoever was handed the interface — which is the state machine and nothing else.
/// </para>
/// </remarks>
public interface ITrayLossConsumer
{
    /// <summary>
    /// Acts on a loss of the affordance. Returning normally IS the confirmation; throwing, or never having
    /// been registered, is what escalates to the authoritative exit.
    /// </summary>
    /// <param name="state">The delivered state — <c>Lost</c> or <c>Unavailable</c>.</param>
    void AcknowledgeLoss(TrayAffordanceState state);
}
