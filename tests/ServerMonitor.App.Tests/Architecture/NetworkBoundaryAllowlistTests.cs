using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
/// <item><description>
/// <b>What the UI frameworks fetch by themselves.</b> The WinUI and Windows API assemblies are allowed as a whole, so the
/// types and members in them that load a URI on their own are watched: <c>WebView2</c> and the
/// <c>Microsoft.Web.WebView2</c> namespace; every image source (<c>ImageSource</c> and the
/// <c>Microsoft.UI.Xaml.Media.Imaging</c> namespace, which holds <c>BitmapImage</c> and <c>SvgImageSource</c>), so a
/// string converted to an image (<c>XamlBindingHelper.ConvertValue(typeof(ImageSource), …)</c>, or an <c>x:Bind</c> of
/// a string to an image <c>Source</c>, whose generated code names the type) is seen too; the
/// <c>Windows.Media.Streaming</c> and <c>Windows.Services.Store</c> namespaces; and, by name pattern, every member of a
/// <c>Windows.*</c> or <c>Microsoft.*</c> type whose name contains <c>FromUri</c> (<c>CreateFromUri</c>,
/// <c>MediaSource.CreateFromUri</c>, <c>XmlDocument.LoadFromUriAsync</c>,
/// <c>StorageFile.CreateStreamedFileFromUriAsync</c>, <c>RandomAccessStreamReference.CreateFromUri</c>, …).
/// <c>Windows.System.Launcher</c> is watched too and allowed where it is used: it hands a URI to the user's default
/// browser or to our own protocol handler, and the app itself makes no connection.
/// </description></item>
/// <item><description>
/// <b>Image and media sources in the App's XAML.</b> Markup is compiled to a binary file, not to IL, so an element that
/// loads a URI (<c>Image</c>, <c>ImageBrush</c>, <c>BitmapIcon</c>, <c>ImageIcon</c>, <c>PersonPicture</c>,
/// <c>MediaPlayerElement</c>, <c>WebView2</c>, <c>BitmapImage</c>, <c>SvgImageSource</c>) whose source is bound
/// (<c>{x:Bind}</c>, <c>{Binding}</c>, <c>{TemplateBinding}</c>) is listed, and so is any declared
/// <c>BitmapImage</c>, <c>SvgImageSource</c> or <c>WebView2</c> element. Both lists are measured: none today.
/// </description></item>
/// <item><description>
/// <b>Web addresses written in the product.</b> Every <c>http://</c>/<c>https://</c> literal in the compiled code of the
/// layers (the string heap of each assembly) and in the App's XAML sources (attribute values and text), each one on an
/// exact list with what it is for. XML namespace identifiers (<c>schemas.microsoft.com</c>, <c>www.w3.org</c>,
/// <c>schemas.openxmlformats.org</c>) are names, never fetched, and are not listed.
/// </description></item>
/// </list>
/// <para>
/// Not covered here, as documented limits: a type resolved from a string at run time, which the source guard against
/// dynamic loading (R6c) forbids; an address that is not written in the product but built or read at run time
/// (concatenated, taken from the resource files, which are text, or from the user's own input such as the servers they
/// add); a URI-loading member of a framework type whose name does not contain <c>FromUri</c> and that is not listed
/// above; and a URI given to a framework element through a generic setter (<c>SetValue</c> with a property found at run
/// time) or a style, which names no watched type. A run-time address reaches the network only through what is watched
/// here or through those limits. A new network path is a product decision that changes these lists in review, never a
/// side effect.
/// </para>
/// </summary>
public sealed class NetworkBoundaryAllowlistTests
{
    private static readonly string[] WatchedNamespaces =
    [
        "System.Net", "Windows.Web", "Windows.Networking",
        "Microsoft.Web.WebView2", "Windows.Media.Streaming", "Windows.Services.Store",
        "Microsoft.UI.Xaml.Media.Imaging",
    ];

    /// <summary>Single framework types that load a URI on their own (their namespaces are otherwise not watched).</summary>
    private static readonly string[] WatchedTypes =
    [
        "Microsoft.UI.Xaml.Controls.WebView2",
        "Microsoft.UI.Xaml.Media.Imaging.BitmapImage",
        "Microsoft.UI.Xaml.Media.Imaging.SvgImageSource",
        "Microsoft.UI.Xaml.Media.ImageSource",
        "Windows.System.Launcher",
    ];

    /// <summary>The name pattern of framework members that load a URI: on a Windows.* or Microsoft.* type, containing this.</summary>
    private const string FetchingMemberPattern = "FromUri";

    /// <summary>XAML elements that load a URI, and the attributes that carry it.</summary>
    private static readonly Dictionary<string, string[]> FetchingElements = new(StringComparer.Ordinal)
    {
        ["Image"] = ["Source"],
        ["ImageBrush"] = ["ImageSource"],
        ["BitmapIcon"] = ["UriSource"],
        ["ImageIcon"] = ["Source"],
        ["PersonPicture"] = ["ProfilePicture"],
        ["MediaPlayerElement"] = ["Source", "PosterSource"],
        ["WebView2"] = ["Source"],
        ["BitmapImage"] = ["UriSource"],
        ["SvgImageSource"] = ["UriSource"],
    };

    /// <summary>XAML elements that fetch by themselves wherever they are declared.</summary>
    private static readonly string[] FetchingDeclarations = ["BitmapImage", "SvgImageSource", "WebView2"];

    /// <summary>Framework members that fetch a URI, "type::member".</summary>
    private static readonly string[] WatchedMembers =
    [
        "Windows.Storage.StorageFile::CreateStreamedFileFromUriAsync",
        "Windows.Storage.StorageFile::ReplaceWithStreamedFileFromUriAsync",
        "Windows.Storage.Streams.RandomAccessStreamReference::CreateFromUri",
    ];

    /// <summary>XML namespace identifiers: names, never fetched.</summary>
    private static readonly string[] XmlNamespaceHosts = ["schemas.microsoft.com", "www.w3.org", "schemas.openxmlformats.org"];

    private static readonly Regex WebAddress = new(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
        "ServerMonitor.App: Windows.System.Launcher",                            // opens the project page in the user's browser
#if DEBUG
        // The QA gallery (Debug only, removed from Release by the project) renders a local element to check tokens; no URI.
        "ServerMonitor.App: Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap",
#endif
        "ServerAlyzer.WidgetProvider: Windows.System.Launcher",                  // opens the app through its own protocol
    ];

    /// <summary>Measured framework members that fetch a URI, "layer: type::member". None.</summary>
    private static readonly string[] MemberAllowlist =
    [
    ];

    /// <summary>Measured web addresses in compiled code, "layer: address", with where each one goes.</summary>
    private static readonly string[] CompiledAddressAllowlist =
    [
        "ServerMonitor.App: https://github.com/pedroloyola/ServerMonitor", // About: opened with Launcher in the browser
    ];

    /// <summary>Measured web addresses in the App's XAML, "file: address". None.</summary>
    private static readonly string[] XamlAddressAllowlist =
    [
    ];

    /// <summary>Measured bound sources of fetching elements in the App's XAML, "file: Element.Attribute". None.</summary>
    private static readonly string[] XamlBoundSourceAllowlist =
    [
    ];

    /// <summary>Measured declarations of self-fetching elements in the App's XAML, "file: Element". None.</summary>
    private static readonly string[] XamlFetchingDeclarationAllowlist =
    [
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
    public void Every_layer_calls_only_the_measured_framework_members_that_fetch_a_uri()
    {
        var found = Layers().SelectMany(layer => layer.MemberReferences
            .Where(IsFetchingMember)
            .Select(member => $"{layer.Name}: {member}"));

        AssertExactly(MemberAllowlist, found);
    }

    [Fact]
    public void Every_bound_source_of_a_fetching_element_in_the_apps_xaml_is_on_the_list()
    {
        var found = AppSourceTree.Files(".xaml").SelectMany(file =>
            XDocument.Load(AppSourceTree.Full(file)).Descendants()
                .Where(element => FetchingElements.ContainsKey(element.Name.LocalName))
                .SelectMany(element => FetchingElements[element.Name.LocalName]
                    .Where(attribute => IsBound(element.Attribute(attribute)?.Value)
                        || element.Elements().Any(child => child.Name.LocalName == $"{element.Name.LocalName}.{attribute}"
                            && child.DescendantsAndSelf().Any(d => d.Attributes().Any(a => IsBound(a.Value)))))
                    .Select(attribute => $"{file}: {element.Name.LocalName}.{attribute}")));

        AssertExactly(XamlBoundSourceAllowlist, found);
    }

    [Fact]
    public void Every_self_fetching_element_declared_in_the_apps_xaml_is_on_the_list()
    {
        var found = AppSourceTree.Files(".xaml").SelectMany(file =>
            XDocument.Load(AppSourceTree.Full(file)).Descendants()
                .Where(element => FetchingDeclarations.Contains(element.Name.LocalName, StringComparer.Ordinal))
                .Select(element => $"{file}: {element.Name.LocalName}"));

        AssertExactly(XamlFetchingDeclarationAllowlist, found);
    }

    [Fact]
    public void The_fetching_rules_recognise_what_they_are_meant_to()
    {
        // Guards the guards: the member pattern and the binding rule on known shapes.
        Assert.True(IsFetchingMember("Windows.Media.Core.MediaSource::CreateFromUri"));
        Assert.True(IsFetchingMember("Windows.Data.Xml.Dom.XmlDocument::LoadFromUriAsync"));
        Assert.True(IsFetchingMember("Windows.Storage.StorageFile::CreateStreamedFileFromUriAsync"));
        Assert.All(WatchedMembers, member => Assert.True(IsFetchingMember(member)));
        Assert.False(IsFetchingMember("Windows.System.Launcher::LaunchUriAsync"));
        Assert.False(IsFetchingMember("ServerMonitor.App.Helpers::LoadFromUri")); // product code is scanned as a layer
        Assert.True(IsBound("{x:Bind RemoteLogo}"));
        Assert.True(IsBound(" {Binding Logo}"));
        Assert.True(IsBound("{TemplateBinding Source}"));
        Assert.False(IsBound("ms-appx:///Assets/Logo.png"));
        Assert.False(IsBound(null));
        Assert.True(IsWatched("Microsoft.UI.Xaml.Media.ImageSource"));
        Assert.True(IsWatched("Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap"));
    }

    [Fact]
    public void Every_web_address_in_compiled_code_is_on_the_list()
    {
        var found = Layers().SelectMany(layer => layer.UserStrings
            .SelectMany(WebAddresses)
            .Select(address => $"{layer.Name}: {address}"));

        AssertExactly(CompiledAddressAllowlist, found);
    }

    [Fact]
    public void Every_web_address_in_the_apps_xaml_is_on_the_list()
    {
        var found = AppSourceTree.Files(".xaml").SelectMany(file =>
        {
            var document = XDocument.Load(AppSourceTree.Full(file));
            var values = document.Descendants().SelectMany(element => element.Attributes()
                    .Where(attribute => !attribute.IsNamespaceDeclaration)
                    .Select(attribute => attribute.Value)
                    .Concat(element.Nodes().OfType<XText>().Select(text => text.Value)));
            return values.SelectMany(WebAddresses).Select(address => $"{file}: {address}");
        });

        AssertExactly(XamlAddressAllowlist, found);
    }

    [Fact]
    public void The_scan_sees_member_references_strings_and_xaml()
    {
        // Guards the guards above: each source they read must actually be read.
        var app = Layers().Single(layer => layer.Name == "ServerMonitor.App");

        Assert.Contains(app.MemberReferences, member => member.StartsWith("Windows.System.Launcher::", StringComparison.Ordinal));
        Assert.NotEmpty(app.UserStrings);
        Assert.Contains("Controls/SaSidebar.xaml", AppSourceTree.Files(".xaml"));
        Assert.Equal(["https://example.org/x"], WebAddresses("see https://example.org/x and http://schemas.microsoft.com/winfx/2006/xaml"));
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
        IReadOnlySet<string> NativeModules,
        IReadOnlySet<string> MemberReferences,
        IReadOnlySet<string> UserStrings);

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

        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind == HandleKind.TypeReference)
            {
                members.Add(FullName(metadata, (TypeReferenceHandle)member.Parent) + "::" + metadata.GetString(member.Name));
            }
        }

        var strings = new HashSet<string>(StringComparer.Ordinal);
        if (metadata.GetHeapSize(HeapIndex.UserString) > 1)
        {
            for (var handle = MetadataTokens.UserStringHandle(1); !handle.IsNil; handle = metadata.GetNextHandle(handle))
            {
                strings.Add(metadata.GetUserString(handle));
            }
        }

        return new Layer(metadata.GetString(metadata.GetAssemblyDefinition().Name), types, assemblies, modules, members, strings);
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

    /// <summary>The web addresses in <paramref name="text"/>, without XML namespace identifiers.</summary>
    private static IEnumerable<string> WebAddresses(string text) =>
        WebAddress.Matches(text).Select(match => match.Value)
            .Where(address => !(Uri.TryCreate(address, UriKind.Absolute, out var uri)
                && XmlNamespaceHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase))));

    /// <summary>A member of a Windows.* or Microsoft.* type that loads a URI: a listed one, or one matching the name pattern.</summary>
    private static bool IsFetchingMember(string member)
    {
        if (WatchedMembers.Contains(member, StringComparer.Ordinal))
        {
            return true;
        }

        var separator = member.IndexOf("::", StringComparison.Ordinal);
        var type = member[..separator];
        var name = member[(separator + 2)..];
        return (type.StartsWith("Windows.", StringComparison.Ordinal) || type.StartsWith("Microsoft.", StringComparison.Ordinal))
            && name.Contains(FetchingMemberPattern, StringComparison.Ordinal);
    }

    /// <summary>A XAML attribute value that is a binding, not a literal.</summary>
    private static bool IsBound(string? value) =>
        value is not null && Regex.IsMatch(value, @"^\s*\{\s*(x:Bind|Binding|TemplateBinding)\b");

    private static bool IsWatched(string typeName)
    {
        if (WatchedTypes.Contains(typeName.Split('+')[0], StringComparer.Ordinal))
        {
            return true;
        }

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
