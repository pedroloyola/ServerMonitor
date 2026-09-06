namespace ServerMonitor.Features;

/// <summary>
/// Read-only record of which capabilities a build actually composed. It holds no policy and makes no
/// decision: it reports what <see cref="FeatureComposition"/> already did.
/// <para>
/// It exists for the rare surface that must ANNOUNCE a capability rather than simply use it. The normal
/// way a consumer discovers that a capability is absent stays what it already is in this codebase: the
/// service resolves to its inert default and the UI degrades (for example
/// <c>IServerHistoryQueryService.IsAvailable</c>). Reaching for this catalog instead of that pattern is
/// how a second, edition-shaped axis leaks into downstream code (ADR-020 §3).
/// </para>
/// </summary>
public interface IFeatureCatalog
{
    /// <summary>Everything that was composed, in composition order.</summary>
    IReadOnlyList<FeatureDescriptor> Composed { get; }

    /// <summary>True when <paramref name="id"/> was composed in this build.</summary>
    bool IsComposed(FeatureId id);
}
