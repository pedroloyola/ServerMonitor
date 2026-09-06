namespace ServerMonitor.Features;

/// <summary>Immutable <see cref="IFeatureCatalog"/> built from what composition really registered.</summary>
public sealed class FeatureCatalog : IFeatureCatalog
{
    /// <summary>
    /// A catalog with nothing in it. Registered as the composition-root default in the same idiom as the
    /// inert service defaults this codebase already uses, so every composition — including the Debug-only
    /// QA harnesses — can resolve <see cref="IFeatureCatalog"/> instead of failing to.
    /// </summary>
    public static readonly FeatureCatalog Empty = new([]);

    private readonly HashSet<FeatureId> _ids;

    public FeatureCatalog(IReadOnlyList<FeatureDescriptor> composed)
    {
        ArgumentNullException.ThrowIfNull(composed);
        Composed = composed;
        _ids = [.. composed.Select(descriptor => descriptor.Id)];
    }

    public IReadOnlyList<FeatureDescriptor> Composed { get; }

    public bool IsComposed(FeatureId id) => _ids.Contains(id);
}
