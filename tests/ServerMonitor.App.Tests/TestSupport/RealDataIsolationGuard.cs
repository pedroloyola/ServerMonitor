using System.Collections;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.App.Tests.TestSupport;

/// <summary>
/// TEST-REALDATA-AUDIT (UI.3 gate 1C): a NON-lexical guard. It looks at what a composed provider REALLY holds, not
/// at test source text.
/// <list type="number">
/// <item>Structural (nothing but options and the profile-rooted SSH sources is constructed): every storage options
/// type - DISCOVERED by reflection over the App and Infrastructure assemblies, so a new one is covered without
/// editing this file - that the collection registers resolves to paths under the test root; the
/// <see cref="WindowsCredentialStore"/> registration is a factory that trips instead of the production type.</item>
/// <item>Constructed: every effective service of the data plane (Core/Infrastructure service types, and any
/// registration whose constructor takes a storage options type) is resolved, and its object graph is walked: every
/// fully-qualified path string must be under the test root and no <see cref="WindowsCredentialStore"/> may be
/// reachable.</item>
/// </list>
/// The structural phase runs first and alone in <see cref="IsolatedAppComposition.BuildProvider"/>, so a real root
/// is refused before any store that reads at construction (the two settings services do) is built.
/// </summary>
internal static class RealDataIsolationGuard
{
    private static readonly Assembly[] OptionsAssemblies =
        [typeof(App).Assembly, typeof(ServerStorageOptions).Assembly];

    private static readonly string[] PathMemberSuffixes = ["Path", "Directory"];

    /// <summary>Path-bearing options types found by reflection: the guard owns no list of them.</summary>
    public static IReadOnlyList<Type> DiscoverStorageOptionsTypes() =>
    [
        .. OptionsAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                           && type.Name.EndsWith("Options", StringComparison.Ordinal)
                           && PathProperties(type).Any())
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
    ];

    public static void AssertStructurallyIsolated(IServiceCollection services, IServiceProvider provider, IReadOnlyList<string> roots) =>
        Fail(StructuralViolations(services, provider, roots));

    public static void AssertIsolated(IServiceCollection services, IServiceProvider provider, IReadOnlyList<string> roots)
    {
        var structural = StructuralViolations(services, provider, roots);
        Fail(structural.Count > 0 ? structural : ConstructedViolations(services, provider, roots));
    }

    public static IReadOnlyList<string> StructuralViolations(IServiceCollection services, IServiceProvider provider, IReadOnlyList<string> roots)
    {
        var violations = new List<string>();
        var registered = services.Select(descriptor => descriptor.ServiceType).ToHashSet();

        foreach (var optionsType in DiscoverStorageOptionsTypes().Where(registered.Contains))
        {
            var options = provider.GetRequiredService(optionsType);
            foreach (var property in PathProperties(optionsType))
            {
                CheckPath(property.GetValue(options) as string, roots, $"{optionsType.Name}.{property.Name}", violations);
            }
        }

        // Inspected, never constructed: the production registration is the type itself.
        var credentialStore = Effective(services, typeof(WindowsCredentialStore));
        if (credentialStore is not null && credentialStore.ImplementationFactory is null)
        {
            violations.Add("WindowsCredentialStore is registered as the production type (Credential Manager), not the tripwire.");
        }
        else if (credentialStore is not null)
        {
            try
            {
                provider.GetService(typeof(WindowsCredentialStore));
                violations.Add("WindowsCredentialStore resolved without tripping: the Credential Manager is reachable.");
            }
            catch (RealDataTripwireException)
            {
            }
        }

        foreach (var profileRooted in new[] { typeof(ISshConfigImportSource), typeof(ILocalSshKeyDiscovery), typeof(IPrivateKeyFilePicker) })
        {
            if (Effective(services, profileRooted) is not null)
            {
                Walk(provider.GetRequiredService(profileRooted), roots, profileRooted.Name, violations);
            }
        }

        return violations;
    }

    /// <summary>
    /// The effective (last-wins) registrations the constructed phase builds: Core/Infrastructure service types, the
    /// storage options themselves, and anything whose implementation constructor takes a storage options type.
    /// </summary>
    public static IReadOnlyList<ServiceDescriptor> DataPlaneDescriptors(IServiceCollection services)
    {
        var optionsTypes = DiscoverStorageOptionsTypes().ToHashSet();
        // Every ServerMonitor.* assembly the App references (Core, Infrastructure, WidgetContract, Collectors, ...):
        // found, not listed, so a new one is covered.
        var dataPlaneAssemblies = typeof(App).Assembly.GetReferencedAssemblies()
            .Where(name => name.Name?.StartsWith("ServerMonitor.", StringComparison.Ordinal) == true)
            .Select(Assembly.Load)
            .ToHashSet();

        return
        [
            .. services
                .GroupBy(descriptor => descriptor.ServiceType)
                .Select(group => group.Last())
                .Where(descriptor => !descriptor.ServiceType.ContainsGenericParameters
                                     && descriptor.ServiceType != typeof(IHostedService)
                                     // Its own registration is the tripwire, proven by the structural phase.
                                     && descriptor.ServiceType != typeof(WindowsCredentialStore)
                                     && (dataPlaneAssemblies.Contains(descriptor.ServiceType.Assembly)
                                         || optionsTypes.Contains(descriptor.ServiceType)
                                         || TakesStorageOptions(descriptor.ImplementationType, optionsTypes)))
        ];
    }

    public static IReadOnlyList<string> ConstructedViolations(IServiceCollection services, IServiceProvider provider, IReadOnlyList<string> roots)
    {
        var violations = new List<string>();
        var effective = DataPlaneDescriptors(services);

        foreach (var descriptor in effective)
        {
            object? instance;
            try
            {
                instance = provider.GetService(descriptor.ServiceType);
            }
            catch (RealDataTripwireException exception)
            {
                violations.Add($"{descriptor.ServiceType.Name}: {exception.Message}");
                continue;
            }

            Walk(instance, roots, descriptor.ServiceType.Name, violations);
        }

        if (effective.Count == 0)
        {
            violations.Add("No data-plane service was found to construct: the guard would be vacuous.");
        }

        return violations;
    }

    private static void Walk(object? value, IReadOnlyList<string> roots, string origin, List<string> violations)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Visit(value, origin, depth: 0);

        void Visit(object? current, string path, int depth)
        {
            if (current is null || depth > 8 || current is Delegate || current.GetType().IsPrimitive || current is Type)
            {
                return;
            }

            if (current is string text)
            {
                CheckPath(text, roots, path, violations);
                return;
            }

            if (!visited.Add(current))
            {
                return;
            }

            if (current is WindowsCredentialStore)
            {
                violations.Add($"{path}: the real WindowsCredentialStore (Credential Manager) is reachable.");
                return;
            }

            if (current is IEnumerable sequence && current.GetType().Namespace?.StartsWith("ServerMonitor", StringComparison.Ordinal) != true)
            {
                var index = 0;
                foreach (var item in sequence)
                {
                    if (item is null || IsOurs(item.GetType()) || item is string)
                    {
                        Visit(item, $"{path}[{index}]", depth + 1);
                    }

                    if (++index > 256)
                    {
                        break;
                    }
                }

                return;
            }

            if (!IsOurs(current.GetType()))
            {
                return;
            }

            for (var type = current.GetType(); type is not null && IsOurs(type); type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    Visit(field.GetValue(current), $"{path}.{field.Name}", depth + 1);
                }
            }
        }
    }

    private static bool IsOurs(Type type) => type.Namespace?.StartsWith("ServerMonitor", StringComparison.Ordinal) == true;

    private static void CheckPath(string? value, IReadOnlyList<string> roots, string origin, List<string> violations)
    {
        if (string.IsNullOrEmpty(value) || !Path.IsPathFullyQualified(value))
        {
            return;
        }

        var full = Path.GetFullPath(value);
        var prefixes = roots.Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar).ToList();
        if (!prefixes.Any(prefix => full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add($"{origin} = {full} is outside the test roots {string.Join(" | ", prefixes)}");
        }
    }

    private static IEnumerable<PropertyInfo> PathProperties(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.PropertyType == typeof(string)
                               && property.GetIndexParameters().Length == 0
                               && PathMemberSuffixes.Any(suffix => property.Name.EndsWith(suffix, StringComparison.Ordinal)));

    private static bool TakesStorageOptions(Type? implementation, HashSet<Type> optionsTypes) =>
        implementation is not null
        && implementation.GetConstructors().Any(ctor => ctor.GetParameters().Any(p => optionsTypes.Contains(p.ParameterType)));

    private static ServiceDescriptor? Effective(IServiceCollection services, Type serviceType) =>
        services.LastOrDefault(descriptor => descriptor.ServiceType == serviceType);

    private static void Fail(IReadOnlyList<string> violations) =>
        Assert.True(violations.Count == 0, "Real-data isolation violated:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
}
