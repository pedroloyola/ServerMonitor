using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using ServerMonitor.WidgetProvider.Hosting;

namespace ServerMonitor.WidgetProvider.Tests.Architecture;

/// <summary>
/// UI.9 SPEC test 1 — the provider never starts the engine, proven on the REAL built assembly, not on a
/// csproj convention alone: (a) the transitive closure of its referenced assemblies contains none of the
/// app's monitoring/infrastructure assemblies nor SSH.NET; (b) its metadata has no type reference named
/// <c>MonitoringEngine</c>/<c>IMonitoringEngine</c>, whatever assembly would supply it; (c) its project
/// references exactly the two contracts. Each leg fails on its own mutation (see the B1/B2 report).
/// </summary>
public sealed class ProviderBoundaryTests
{
    private static readonly string[] ForbiddenAssemblies =
    {
        "ServerMonitor.Core",
        "ServerMonitor.Infrastructure",
        "ServerMonitor.Collectors",
        "ServerMonitor.App",
        "ServerMonitor.Features",
        "Renci.SshNet" // SSH.NET
    };

    private static readonly Assembly Provider = typeof(WidgetProviderCoordinator).Assembly;

    [Fact]
    public void Provider_assembly_closure_never_reaches_the_app_engine_infrastructure_or_ssh()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<AssemblyName>(Provider.GetReferencedAssemblies());
        var loaded = 0;
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (name.Name is null || !seen.Add(name.Name))
            {
                continue;
            }

            Assembly assembly;
            try
            {
                assembly = Assembly.Load(name);
            }
            catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                continue; // the NAME is still checked below; an unloadable leaf has nothing further to walk
            }

            loaded++;
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                pending.Enqueue(reference);
            }
        }

        // The walk must be real: the provider's own direct references (the two contracts, the runtime) are
        // in the closure, so an empty or broken walk cannot pass vacuously.
        Assert.Contains("ServerMonitor.WidgetContract", seen);
        Assert.Contains("ServerMonitor.ActivationContract", seen);
        Assert.True(loaded > 10, $"closure walk loaded only {loaded} assemblies");

        var offenders = seen
            .Where(n => ForbiddenAssemblies.Any(f => n.Equals(f, StringComparison.OrdinalIgnoreCase)
                || n.StartsWith(f + ".", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Provider_metadata_has_no_type_reference_to_the_monitoring_engine()
    {
        using var stream = File.OpenRead(Provider.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();

        var typeNames = metadata.TypeReferences
            .Select(handle => metadata.GetString(metadata.GetTypeReference(handle).Name))
            .Concat(metadata.TypeDefinitions.Select(handle => metadata.GetString(metadata.GetTypeDefinition(handle).Name)))
            .ToArray();

        Assert.NotEmpty(typeNames); // a real scan, not an empty table
        Assert.Contains(nameof(WidgetProviderCoordinator), typeNames);
        Assert.DoesNotContain(typeNames, n => n is "MonitoringEngine" or "IMonitoringEngine");
    }

    [Fact]
    public void Provider_project_references_exactly_the_two_contracts()
    {
        var csproj = Path.Combine(RepositoryRoot(), "src", "ServerMonitor.WidgetProvider", "ServerMonitor.WidgetProvider.csproj");
        var references = XDocument.Load(csproj)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension((string?)e.Attribute("Include") ?? string.Empty))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "ServerMonitor.ActivationContract", "ServerMonitor.WidgetContract" }, references);
    }

    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (ServerMonitor.slnx) not found above the test output.");
    }
}
