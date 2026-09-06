namespace ServerMonitor.Features;

/// <summary>
/// What a module says about itself before anything is registered.
/// </summary>
/// <param name="Id">The capability this module composes.</param>
/// <param name="RequiresEntitlement">
/// <para>
/// <b>false</b> for every capability that ships in this repository. Such a module is composed
/// UNCONDITIONALLY and the entitlement provider is never consulted for it.
/// </para>
/// <para>
/// This flag is the mechanism behind the binding condition attached to the human classification
/// <c>M14-ENT-1</c>: entitlement may gate the availability of commercial functionality and nothing else.
/// The first draft of ADR-020 put every module behind the provider, which would have made a provider that
/// returned <c>false</c> — or threw — hide the user's OWN local history, workloads and widget snapshot.
/// That is entitlement acting as authority over access to user data, which the classification forbids.
/// Setting this to <c>true</c> for a capability that already ships here is therefore not a tuning
/// decision; it is a policy change.
/// </para>
/// </param>
public readonly record struct FeatureDescriptor(FeatureId Id, bool RequiresEntitlement)
{
    /// <summary>A capability that is always composed: the provider is not asked about it.</summary>
    public static FeatureDescriptor Unconditional(string id) =>
        new(new FeatureId(id), RequiresEntitlement: false);
}
