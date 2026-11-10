using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ServerMonitor.Collectors;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// Local-first boundary: the product talks to the network only through the two paths it already has, the SSH
/// connection to the user's own servers and the mDNS discovery on the local network. This is checked on the COMPILED
/// assemblies (every type the metadata references), so it cannot be dodged by a <c>using</c> alias, a fully qualified
/// name or a type reached through <c>var</c>.
/// <para>
/// The allowlist is exact, by assembly and type, and was measured from the code as it is. Anything new from
/// <c>System.Net.Http</c> or <c>System.Net.Sockets</c> in Core, Infrastructure, Collectors or the App fails this test:
/// a new network path is a product decision that changes this list in review, never a side effect.
/// </para>
/// </summary>
public sealed class NetworkBoundaryAllowlistTests
{
    private static readonly string[] WatchedNamespaces = ["System.Net.Http", "System.Net.Sockets"];

    private static readonly Assembly[] Layers =
    [
        typeof(Server).Assembly,                 // ServerMonitor.Core
        typeof(InfrastructureAssembly).Assembly, // ServerMonitor.Infrastructure
        typeof(CollectorsAssembly).Assembly,     // ServerMonitor.Collectors
        typeof(App).Assembly,                    // ServerMonitor.App
    ];

    /// <summary>Measured usage, "assembly: type". The reason for each is the comment beside it.</summary>
    /// <remarks>
    /// None of these opens a connection: they are an enum and an exception type. The connections themselves are made
    /// inside the SSH.NET and Tmds.MDns packages, which these layers do not reach around. No layer references
    /// <c>System.Net.Http</c> at all.
    /// </remarks>
    private static readonly string[] Allowlist =
    [
        "ServerMonitor.Core: System.Net.Sockets.AddressFamily",            // SSH config: an IPv6 jump host is bracketed
        "ServerMonitor.Infrastructure: System.Net.Sockets.SocketError",    // SSH: mapping a failed connection to a cause
        "ServerMonitor.Infrastructure: System.Net.Sockets.SocketException", // SSH: the same mapping
        "ServerMonitor.App: System.Net.Sockets.AddressFamily",             // mDNS result / SSH command: IPv6 address shape
    ];

    [Fact]
    public void No_layer_references_an_http_or_socket_type_beyond_the_existing_ssh_and_mdns_uses()
    {
        var found = Layers.SelectMany(NetworkTypesReferencedBy).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(Allowlist.Order(StringComparer.Ordinal).ToArray(), found);
    }

    [Fact]
    public void The_scan_sees_type_references_at_all()
    {
        // Guards the guard: a scan that finds nothing anywhere would pass the test above vacuously.
        Assert.Contains("System.Threading.Tasks.Task", TypeReferences(typeof(App).Assembly));
    }

    internal static IEnumerable<string> NetworkTypesReferencedBy(Assembly assembly) =>
        TypeReferences(assembly)
            .Where(name => WatchedNamespaces.Any(ns => name.StartsWith(ns + ".", StringComparison.Ordinal)))
            .Select(name => $"{assembly.GetName().Name}: {name}");

    internal static IReadOnlySet<string> TypeReferences(Assembly assembly)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in metadata.TypeReferences)
        {
            names.Add(FullName(metadata, handle));
        }

        return names;
    }

    private static string FullName(MetadataReader metadata, TypeReferenceHandle handle)
    {
        var type = metadata.GetTypeReference(handle);
        var name = metadata.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return FullName(metadata, (TypeReferenceHandle)type.ResolutionScope) + "+" + name;
        }

        var ns = metadata.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }
}
