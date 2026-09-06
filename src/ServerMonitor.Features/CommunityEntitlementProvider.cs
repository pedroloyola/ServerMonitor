namespace ServerMonitor.Features;

/// <summary>
/// The only entitlement provider that ships in this repository.
/// <para>
/// It grants nothing, and that is the correct and complete Community answer. Every capability in this
/// repository declares <see cref="FeatureDescriptor.RequiresEntitlement"/> = <c>false</c> and is therefore
/// composed without ever consulting a provider, so the only questions this type can be asked are about
/// capabilities that do not exist here. Denying them is failing closed, and it costs the user nothing
/// precisely because the Community build is fully functional on its own (ADR-021 E1/E4).
/// </para>
/// <para>
/// It is deterministic and needs no account, no network, no Cloud, no Store API and no secret. It reads
/// nothing, caches nothing, and has no state to go stale: the same question always gets the same answer.
/// </para>
/// </summary>
public sealed class CommunityEntitlementProvider : IEntitlementProvider
{
    /// <summary>Shared instance. The type is immutable and stateless, so one is enough.</summary>
    public static readonly CommunityEntitlementProvider Instance = new();

    public bool IsEntitled(FeatureId id) => false;
}
