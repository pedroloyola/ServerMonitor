using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ServerMonitor.ActivationContract;
using ServerMonitor.Collectors;
using ServerMonitor.Core.Models;
using ServerMonitor.Features;
using ServerMonitor.Infrastructure;
using ServerMonitor.WidgetContract;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// Local-first boundary, checked on the COMPILED product assemblies: Core, Infrastructure, Collectors, Features, the App,
/// the widget provider and the two contract assemblies. Reading metadata (not source) means a <c>using</c> alias, a fully
/// qualified name, <c>var</c>, a generic argument or a member reference cannot hide a type.
/// <para>What is checked, each against an exact list measured from the code as it is:</para>
/// <list type="number">
/// <item><description>
/// <b>Types.</b> Every type the assembly references in the <c>System.Net</c>, <c>Windows.Web</c> and
/// <c>Windows.Networking</c> namespaces (and their sub-namespaces). Today these are only address, interface and error
/// types; none of them opens a connection.
/// </description></item>
/// <item><description>
/// <b>Assemblies.</b> No layer may reference a System assembly that makes or accepts connections
/// (<see cref="DeniedAssemblies"/>). Every other referenced assembly outside the <c>System</c> family must be on the
/// layer's list, so a new package, which could connect on our behalf where this scan cannot see, is a reviewed change.
/// The packages on the lists that do connect are the two existing paths: Renci.SshNet (SSH to the user's own servers)
/// and Tmds.MDns (discovery on the local network). A product assembly referenced by a layer must itself be a scanned
/// layer, so a helper cannot carry network code into a layer from outside the scan.
/// </description></item>
/// <item><description>
/// <b>Native modules.</b> Every native library a layer imports (<c>DllImport</c>/<c>LibraryImport</c>).
/// </description></item>
/// </list>
/// <para>
/// Not covered here: a type resolved from a string at run time, which the source guard against dynamic loading (R6c)
/// forbids. A new network path is a product decision that changes these lists in review, never a side effect.
/// </para>
/// </summary>
public sealed class NetworkBoundaryAllowlistTests
{
    private static readonly string[] WatchedNamespaces = ["System.Net", "Windows.Web", "Windows.Networking"];

    /// <summary>Assemblies whose purpose is to make or accept connections. Matched on the name or as a dotted prefix.</summary>
    private static readonly string[] DeniedAssemblies =
    [
        "System.Net.Http", "System.Net.Sockets", "System.Net.Requests", "System.Net.WebClient", "System.Net.WebSockets",
        "System.Net.NameResolution", "System.Net.Ping", "System.Net.Quic", "System.Net.Security", "System.Net.Mail",
        "System.Net.HttpListener", "System.Net.WebProxy", "Microsoft.Extensions.Http",
    ];

    private const string WidgetProviderAssemblyName = "ServerAlyzer.WidgetProvider";

    /// <summary>Measured network-namespace types, "layer: type". None of them opens a connection.</summary>
    private static readonly string[] TypeAllowlist =
    [
        "ServerMonitor.Core: System.Net.IPAddress",                              // discovery: the addresses of a found service
        "ServerMonitor.Core: System.Net.Sockets.AddressFamily",                  // SSH config: an IPv6 jump host is bracketed
        "ServerMonitor.Infrastructure: System.Net.IPAddress",                    // SSH tunnel loopback / mDNS result addresses
        "ServerMonitor.Infrastructure: System.Net.NetworkInformation.NetworkInterface", // mDNS: the interface an answer came on
        "ServerMonitor.Infrastructure: System.Net.Sockets.SocketError",          // SSH: mapping a failed connection to a cause
        "ServerMonitor.Infrastructure: System.Net.Sockets.SocketException",      // SSH: the same mapping
        "ServerMonitor.App: System.Net.IPAddress",                               // mDNS result / SSH command: address shape
        "ServerMonitor.App: System.Net.Sockets.AddressFamily",                   // mDNS result / SSH command: IPv6 address shape
    ];

    /// <summary>Measured assembly references outside the System family, "layer -> assembly".</summary>
    private static readonly string[] AssemblyAllowlist =
    [
        // The two packages that connect: the existing paths, both in Infrastructure.
        "ServerMonitor.Infrastructure -> Renci.SshNet",        // SSH to the user's own servers
        "ServerMonitor.Infrastructure -> Tmds.MDns",           // discovery on the local network
        // Local storage and logging.
        "ServerMonitor.Infrastructure -> Microsoft.Data.Sqlite",
        "ServerMonitor.Infrastructure -> Microsoft.Extensions.Logging.Abstractions",
        "ServerMonitor.Features -> Microsoft.Extensions.DependencyInjection.Abstractions",
        // The application host, WinUI and the Windows App SDK projections (local UI, notifications, lifecycle, resources).
        "ServerMonitor.App -> Microsoft.Extensions.DependencyInjection.Abstractions",
        "ServerMonitor.App -> Microsoft.Extensions.Hosting",
        "ServerMonitor.App -> Microsoft.Extensions.Hosting.Abstractions",
        "ServerMonitor.App -> Microsoft.Extensions.Logging",
        "ServerMonitor.App -> Microsoft.Extensions.Logging.Abstractions",
        "ServerMonitor.App -> Microsoft.Extensions.Logging.Debug",
        "ServerMonitor.App -> Microsoft.InteractiveExperiences.Projection",
        "ServerMonitor.App -> Microsoft.WinUI",
        "ServerMonitor.App -> Microsoft.Windows.AppLifecycle.Projection",
        "ServerMonitor.App -> Microsoft.Windows.AppNotifications.Builder.Projection",
        "ServerMonitor.App -> Microsoft.Windows.AppNotifications.Projection",
        "ServerMonitor.App -> Microsoft.Windows.ApplicationModel.Resources.Projection",
        "ServerMonitor.App -> Microsoft.Windows.SDK.NET",       // Windows API projection; its network types are watched above
        "ServerMonitor.App -> WinRT.Runtime",
        "ServerMonitor.App -> WinUIEx",
        "ServerAlyzer.WidgetProvider -> Microsoft.Windows.SDK.NET",
        "ServerAlyzer.WidgetProvider -> Microsoft.Windows.Widgets.Projection",
        "ServerAlyzer.WidgetProvider -> WinRT.Runtime",
        // Product assemblies, each of them a scanned layer.
        "ServerMonitor.Collectors -> ServerMonitor.Core",
        "ServerMonitor.Collectors -> ServerMonitor.Infrastructure",
        "ServerMonitor.Infrastructure -> ServerMonitor.Core",
        "ServerMonitor.Infrastructure -> ServerMonitor.WidgetContract",
        "ServerMonitor.App -> ServerMonitor.ActivationContract",
        "ServerMonitor.App -> ServerMonitor.Collectors",
        "ServerMonitor.App -> ServerMonitor.Core",
        "ServerMonitor.App -> ServerMonitor.Features",
        "ServerMonitor.App -> ServerMonitor.Infrastructure",
        "ServerMonitor.App -> ServerMonitor.WidgetContract",
        "ServerAlyzer.WidgetProvider -> ServerMonitor.ActivationContract",
        "ServerAlyzer.WidgetProvider -> ServerMonitor.WidgetContract",
    ];

    /// <summary>Measured native imports, "layer => module".</summary>
    private static readonly string[] NativeModuleAllowlist =
    [
        "ServerMonitor.Infrastructure => advapi32.dll",           // Windows Credential Manager
        "ServerMonitor.Infrastructure => iphlpapi.dll",           // SSH tunnel: reads the TCP table to prove loopback ownership
        "ServerMonitor.Infrastructure => kernel32.dll",
        "ServerMonitor.App => Microsoft.WindowsAppRuntime.dll",   // Windows App SDK bootstrap
        "ServerMonitor.App => Shcore.dll",                        // DPI
        "ServerMonitor.App => kernel32.dll",
        "ServerMonitor.App => ole32.dll",
        "ServerMonitor.App => shell32.dll",
        "ServerMonitor.App => user32.dll",
        "ServerAlyzer.WidgetProvider => Microsoft.WindowsAppRuntime.dll",
        "ServerAlyzer.WidgetProvider => ole32.dll",               // COM server registration
    ];

    [Fact]
    public void Every_layer_references_only_the_measured_network_namespace_types()
    {
        var found = Layers().SelectMany(layer => layer.TypeReferences
            .Where(IsWatched)
            .Select(name => $"{layer.Name}: {name}"));

        AssertExactly(TypeAllowlist, found);
    }

    [Fact]
    public void No_layer_references_an_assembly_that_makes_connections()
    {
        var denied = Layers().SelectMany(layer => layer.AssemblyReferences
            .Where(reference => DeniedAssemblies.Any(prefix => IsOrStartsWith(reference, prefix)))
            .Select(reference => $"{layer.Name} -> {reference}"));

        Assert.Empty(denied);
    }

    [Fact]
    public void Every_layer_references_only_the_measured_assemblies_outside_the_system_family()
    {
        var found = Layers().SelectMany(layer => layer.AssemblyReferences
            .Where(reference => !IsSystemFamily(reference))
            .Select(reference => $"{layer.Name} -> {reference}"));

        AssertExactly(AssemblyAllowlist, found);
    }

    [Fact]
    public void A_product_assembly_a_layer_references_is_itself_a_scanned_layer()
    {
        var layers = Layers();
        var scanned = layers.Select(layer => layer.Name).ToHashSet(StringComparer.Ordinal);
        var outside = layers.SelectMany(layer => layer.AssemblyReferences
            .Where(IsProductAssembly)
            .Where(reference => !scanned.Contains(reference))
            .Select(reference => $"{layer.Name} -> {reference}"));

        Assert.Empty(outside);
    }

    [Fact]
    public void Every_layer_imports_only_the_measured_native_modules()
    {
        var found = Layers().SelectMany(layer => layer.NativeModules.Select(module => $"{layer.Name} => {module}"));

        AssertExactly(NativeModuleAllowlist, found);
    }

    [Fact]
    public void The_scan_reads_every_layer()
    {
        // Guards the guards: a layer whose scan came back empty would pass every test above vacuously.
        var layers = Layers();

        Assert.Equal(
            [
                "ServerAlyzer.WidgetProvider", "ServerMonitor.ActivationContract", "ServerMonitor.App", "ServerMonitor.Collectors",
                "ServerMonitor.Core", "ServerMonitor.Features", "ServerMonitor.Infrastructure", "ServerMonitor.WidgetContract",
            ],
            layers.Select(layer => layer.Name).Order(StringComparer.Ordinal));
        Assert.All(layers, layer =>
        {
            Assert.Contains("System.Object", layer.TypeReferences);
            Assert.NotEmpty(layer.AssemblyReferences);
        });
    }

    // ---------------------------------------------------------------- the scan

    internal sealed record Layer(
        string Name,
        IReadOnlySet<string> TypeReferences,
        IReadOnlySet<string> AssemblyReferences,
        IReadOnlySet<string> NativeModules);

    internal static IReadOnlyList<Layer> Layers() =>
    [
        Read(typeof(Server).Assembly.Location),             // ServerMonitor.Core
        Read(typeof(InfrastructureAssembly).Assembly.Location),
        Read(typeof(CollectorsAssembly).Assembly.Location),
        Read(typeof(FeatureId).Assembly.Location),          // ServerMonitor.Features
        Read(typeof(App).Assembly.Location),                // ServerMonitor.App
        Read(typeof(IWidgetStateWriter).Assembly.Location), // ServerMonitor.WidgetContract
        Read(typeof(ActivationUri).Assembly.Location),      // ServerMonitor.ActivationContract
        Read(WidgetProviderPath()),
    ];

    internal static Layer Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in metadata.TypeReferences)
        {
            types.Add(FullName(metadata, handle));
        }

        var assemblies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in metadata.AssemblyReferences)
        {
            assemblies.Add(metadata.GetString(metadata.GetAssemblyReference(handle).Name));
        }

        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var row = 1; row <= metadata.GetTableRowCount(TableIndex.ModuleRef); row++)
        {
            modules.Add(metadata.GetString(metadata.GetModuleReference(MetadataTokens.ModuleReferenceHandle(row)).Name));
        }

        return new Layer(metadata.GetString(metadata.GetAssemblyDefinition().Name), types, assemblies, modules);
    }

    /// <summary>
    /// The widget provider is a separate executable this project does not reference, so it is read from the solution
    /// build's output for the same configuration as these tests. A missing file fails: an unscanned layer is not a pass.
    /// </summary>
    private static string WidgetProviderPath()
    {
        var configuration = AppContext.BaseDirectory
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Last(segment => segment is "Debug" or "Release");
        var bin = Path.Combine(AppSourceTree.RepositoryRoot, "src", "ServerMonitor.WidgetProvider", "bin");
        var candidate = Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, WidgetProviderAssemblyName + ".dll", SearchOption.AllDirectories)
                .Where(path => path.Split(Path.DirectorySeparatorChar).Contains(configuration, StringComparer.Ordinal))
                .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "ref" or "publish"))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        return candidate ?? throw new FileNotFoundException(
            $"The widget provider ({configuration}) was not built; build ServerMonitor.slnx before running these tests.");
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

    private static bool IsWatched(string typeName)
    {
        var outer = typeName.Split('+')[0];
        var lastDot = outer.LastIndexOf('.');
        var ns = lastDot < 0 ? string.Empty : outer[..lastDot];
        return WatchedNamespaces.Any(watched => IsOrStartsWith(ns, watched));
    }

    private static bool IsOrStartsWith(string name, string prefix) =>
        name.Equals(prefix, StringComparison.Ordinal) || name.StartsWith(prefix + ".", StringComparison.Ordinal);

    private static bool IsSystemFamily(string assembly) =>
        IsOrStartsWith(assembly, "System") || assembly is "mscorlib" or "netstandard";

    private static bool IsProductAssembly(string assembly) =>
        IsOrStartsWith(assembly, "ServerMonitor") || IsOrStartsWith(assembly, "ServerAlyzer");

    private static void AssertExactly(IEnumerable<string> allowlist, IEnumerable<string> found)
    {
        var expected = allowlist.ToHashSet(StringComparer.Ordinal);
        var actual = found.ToHashSet(StringComparer.Ordinal);
        var unexpected = actual.Except(expected).Order(StringComparer.Ordinal).ToArray();
        var missing = expected.Except(actual).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            unexpected.Length == 0 && missing.Length == 0,
            "Not on the list (a new network path needs review):" + Environment.NewLine +
            string.Join(Environment.NewLine, unexpected.Select(item => "  + " + item)) + Environment.NewLine +
            "On the list but no longer referenced (remove it):" + Environment.NewLine +
            string.Join(Environment.NewLine, missing.Select(item => "  - " + item)));
    }
}
