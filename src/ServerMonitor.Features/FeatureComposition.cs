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
    /// <para>
    /// <b>R8, enforced for entitlement-requiring modules.</b> Such a module may only ADD. Its
    /// <see cref="IFeatureModule.Register"/> is checked against a snapshot taken just before the call, and
    /// an <see cref="InvalidOperationException"/> naming the feature and the service type is thrown when it
    /// removed, replaced or reordered a descriptor that was already present; when it added a descriptor for
    /// a service type that was already registered (DI resolves the last one, so that would shadow the
    /// existing implementation); or when it registered anything assignable to
    /// <see cref="IEntitlementProvider"/>, which must never be reachable from the container. The rule is
    /// strict on purpose: a legitimate need for multi-registration would be an explicit, reviewed allowlist,
    /// not a relaxation here. Unconditional modules are not checked, so their behaviour and cost are
    /// unchanged.
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

            if (descriptor.RequiresEntitlement)
            {
                RegisterAdditively(module, descriptor.Id, services);
            }
            else
            {
                module.Register(services);
            }

            composed.Add(descriptor);
        }

        return new FeatureCompositionResult(new FeatureCatalog(composed), skipped);
    }

    /// <summary>
    /// Runs <paramref name="module"/>'s registration and proves it only appended (R8). A violation is a
    /// defect in our own module, so it is loud, exactly like an exception thrown by Register itself.
    /// </summary>
    private static void RegisterAdditively(IFeatureModule module, FeatureId id, IServiceCollection services)
    {
        var before = new ServiceDescriptor[services.Count];
        services.CopyTo(before, 0);
        var existingTypes = new HashSet<Type>(before.Select(descriptor => descriptor.ServiceType));

        module.Register(services);

        // (a) Everything that was there is still there, by reference, in the same place.
        for (var i = 0; i < before.Length; i++)
        {
            if (i >= services.Count || !ReferenceEquals(services[i], before[i]))
            {
                throw new InvalidOperationException(
                    $"Feature '{id}' removed, replaced or reordered the existing registration of " +
                    $"'{before[i].ServiceType.FullName}'. An entitlement-requiring module may only add.");
            }
        }

        for (var i = before.Length; i < services.Count; i++)
        {
            var added = services[i];

            // (b) No new descriptor for a type that is already registered: last-one-wins would shadow it.
            if (existingTypes.Contains(added.ServiceType))
            {
                throw new InvalidOperationException(
                    $"Feature '{id}' registered '{added.ServiceType.FullName}', which is already registered. " +
                    "An entitlement-requiring module may not shadow an existing service.");
            }

            // (c) The provider is consulted once, here, and is never reachable from the container.
            if (ExposesEntitlementProvider(added))
            {
                throw new InvalidOperationException(
                    $"Feature '{id}' registered an entitlement provider as '{added.ServiceType.FullName}'. " +
                    "The entitlement provider must never be registered in the container.");
            }
        }
    }

    private static bool ExposesEntitlementProvider(ServiceDescriptor descriptor)
    {
        // Keyed descriptors throw from the non-keyed accessors, so read the matching pair.
        var implementationType = descriptor.IsKeyedService
            ? descriptor.KeyedImplementationType
            : descriptor.ImplementationType;
        var implementationInstance = descriptor.IsKeyedService
            ? descriptor.KeyedImplementationInstance
            : descriptor.ImplementationInstance;

        return typeof(IEntitlementProvider).IsAssignableFrom(descriptor.ServiceType)
            || (implementationType is not null && typeof(IEntitlementProvider).IsAssignableFrom(implementationType))
            || implementationInstance is IEntitlementProvider;
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
