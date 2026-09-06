using Microsoft.Extensions.DependencyInjection;

namespace ServerMonitor.Features;

/// <summary>One module that was not composed because the entitlement question failed or was answered no.</summary>
/// <param name="Id">The capability that was skipped.</param>
/// <param name="Error">
/// The exception the provider threw, or <c>null</c> when it simply answered no. Surfaced rather than
/// swallowed so the caller can log it: a failure that disappears into a <c>catch</c> is the silent-success
/// pattern, and the only reason swallowing is acceptable at all here is that the outcome is the SAFE one.
/// </param>
public readonly record struct FeatureCompositionFailure(FeatureId Id, Exception? Error);

/// <summary>What composition produced: the catalog, and everything that did not make it.</summary>
public readonly record struct FeatureCompositionResult(
    IFeatureCatalog Catalog,
    IReadOnlyList<FeatureCompositionFailure> Skipped);

/// <summary>
/// Turns a static list of modules into registrations plus a catalog. This is the ONLY place in the product
/// where an entitlement question is asked.
/// </summary>
public static class FeatureComposition
{
    /// <summary>
    /// Composes <paramref name="modules"/> into <paramref name="services"/>.
    /// <para>
    /// Two branches, and the split is the whole point (ADR-020 §2, human classification <c>M14-ENT-1</c>):
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <see cref="FeatureDescriptor.RequiresEntitlement"/> is <c>false</c> — every capability in this
    /// repository — the module registers UNCONDITIONALLY and <paramref name="entitlements"/> is not
    /// consulted at all. A provider that denies everything, or one that throws on every call, therefore
    /// cannot change what a Community build composes. That is not a courtesy: gating these would make
    /// entitlement the authority over the user's access to their own local data, which the classification
    /// forbids outright.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <see cref="FeatureDescriptor.RequiresEntitlement"/> is <c>true</c> — only possible in a build that
    /// supplies its own modules — the provider is asked. No, or a thrown exception, both mean NOT composed.
    /// Never the reverse.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// An exception thrown by <see cref="IFeatureModule.Register"/> is a defect in our own module and is
    /// deliberately NOT caught: it propagates and fails the build of the host. Only the entitlement
    /// question is allowed to fail softly, and only towards "absent".
    /// </para>
    /// </summary>
    public static FeatureCompositionResult Compose(
        IServiceCollection services,
        IReadOnlyList<IFeatureModule> modules,
        IEntitlementProvider entitlements)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(entitlements);

        var composed = new List<FeatureDescriptor>(modules.Count);
        var skipped = new List<FeatureCompositionFailure>();
        var seen = new HashSet<FeatureId>();

        foreach (var module in modules)
        {
            ArgumentNullException.ThrowIfNull(module);
            var descriptor = module.Descriptor;

            if (!descriptor.Id.IsSpecified)
            {
                throw new InvalidOperationException(
                    $"Feature module '{module.GetType().FullName}' declares an unspecified feature id.");
            }

            if (!seen.Add(descriptor.Id))
            {
                // A duplicate id means two modules claim the same capability, and DI's last-one-wins
                // would silently pick one. That is a wiring defect, so it is loud rather than resolved.
                throw new InvalidOperationException(
                    $"Feature '{descriptor.Id}' is declared by more than one module. Each capability is " +
                    "composed by exactly one module.");
            }

            if (descriptor.RequiresEntitlement && !TryEntitle(entitlements, descriptor.Id, out var error))
            {
                skipped.Add(new FeatureCompositionFailure(descriptor.Id, error));
                continue;
            }

            module.Register(services);
            composed.Add(descriptor);
        }

        return new FeatureCompositionResult(new FeatureCatalog(composed), skipped);
    }

    private static bool TryEntitle(IEntitlementProvider entitlements, FeatureId id, out Exception? error)
    {
        try
        {
            error = null;
            return entitlements.IsEntitled(id);
        }
        catch (Exception exception)
        {
            // Fail CLOSED. The caller receives the exception in the result and can log it, so this is not
            // a failure disappearing into a catch; it is a failure resolving to the safe outcome and being
            // reported. Returning true here — or letting a partially-registered module through — would be
            // the fail-open defect ADR-021 E2 forbids.
            error = exception;
            return false;
        }
    }
}
