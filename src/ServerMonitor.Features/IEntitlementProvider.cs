namespace ServerMonitor.Features;

/// <summary>
/// Answers whether this installation may compose a capability that declares
/// <see cref="FeatureDescriptor.RequiresEntitlement"/>.
/// <para>
/// <b>Scope, and it is a hard limit.</b> Under the human classification <c>M14-ENT-1</c> this contract may
/// influence exactly one thing: whether a commercial capability is composed. It must never become the
/// authority for OS privilege, SSH authorization, secrets, access to the user's data, remote
/// authorization, trust decisions, destructive operations, or a cryptographic security boundary. A future
/// capability that crosses any of those is reclassified as a security boundary and needs a fresh focused
/// security review.
/// </para>
/// <para>
/// It is deliberately NOT registered in the container. The composition root receives one and hands it to
/// <see cref="FeatureComposition"/>; nothing downstream can resolve it. That turns "the provider is
/// consulted once, at the root" into a property provable by ABSENCE rather than by convention.
/// </para>
/// </summary>
public interface IEntitlementProvider
{
    /// <summary>
    /// True when <paramref name="id"/> may be composed. Implementations fail closed: anything absent,
    /// invalid, expired or unverifiable is not entitled. Throwing is treated as not entitled by
    /// <see cref="FeatureComposition"/> — never as entitled.
    /// </summary>
    bool IsEntitled(FeatureId id);
}
