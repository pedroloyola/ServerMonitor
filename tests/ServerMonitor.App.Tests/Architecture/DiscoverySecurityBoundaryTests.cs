using System.Reflection;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Tests.Architecture;

public sealed class DiscoverySecurityBoundaryTests
{
    private static readonly string[] ForbiddenCapabilities =
    [
        "IServerCredentialStore",
        "IPrivateKeyFilePicker",
        "ISshConnectionService",
        "IHostKeyTrustStore",
        "IServerMetricsCollector",
        "IServerMetricsStore",
        "IMonitoringEngine",
        "PrivateKeyPath",
        "CredentialReferenceId",
        "TrustedHostKey",
        "SshConnectionRequest",
        "TestConnectionAsync",
        "ConnectAsync",
        "TrustAsync",
        "CollectAsync",
        "RefreshNowAsync"
    ];

    [Fact]
    public void DiscoveryAssemblies_DoNotReferenceTmdsOutsideInfrastructure()
    {
        Assert.DoesNotContain(
            typeof(DiscoveryInputPolicy).Assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("Tmds", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(
            typeof(ServerDiscoveryService).Assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("Tmds", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void TmdsTypesAndNamespace_AppearOnlyInsideInfrastructureSource()
    {
        var root = FindRepositoryRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".csproj")
            .Where(path => !path.Contains(
                Path.Combine("src", "ServerMonitor.Infrastructure"),
                StringComparison.OrdinalIgnoreCase))
            // Comments may name the chosen adapter/library without creating an architectural
            // dependency. Strip comments and reject only executable/project references.
            .Where(path => StripComments(File.ReadAllText(path))
                .Contains("Tmds.MDns", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void PublicDiscoveryContracts_ExposeNoThirdPartyTypes()
    {
        var contractTypes = new[]
        {
            typeof(IMdnsServiceBrowser),
            typeof(IServerDiscoveryService),
            typeof(DiscoveryObservation),
            typeof(DiscoveredService),
            typeof(ServiceInstanceIdentity)
        };

        var exposed = contractTypes
            .SelectMany(GetPublicSurfaceTypes)
            .Where(type => type.Assembly.GetName().Name?.StartsWith("Tmds", StringComparison.OrdinalIgnoreCase) == true)
            .Distinct()
            .ToArray();

        Assert.Empty(exposed);
    }

    [Fact]
    public void DiscoveredModels_CannotCarryCredentialsPrivateKeysTrustOrFingerprint()
    {
        var names = new[] { typeof(DiscoveryObservation), typeof(DiscoveredService) }
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Fingerprint", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Trusted", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Username", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Authentication", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ServerDiscoveryService_ConstructorHasOnlyPassiveDiscoveryCapabilities()
    {
        var constructor = Assert.Single(typeof(ServerDiscoveryService).GetConstructors());
        var parameters = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.Equal(
            [typeof(IMdnsServiceBrowser), typeof(IIgnoredDeviceStore),
             typeof(Microsoft.Extensions.Logging.ILogger<ServerDiscoveryService>),
             typeof(TimeProvider), typeof(DiscoveryOptions)],
            parameters);
    }

    [Fact]
    public void DiscoveredOnlyProductionFlow_ContainsNoPrivilegedCapabilityReference()
    {
        var root = FindRepositoryRoot();
        var files = new List<string>();
        files.AddRange(Directory.EnumerateFiles(
            Path.Combine(root, "src", "ServerMonitor.Core", "Discovery"), "*.cs"));
        files.AddRange(new[]
        {
            Path.Combine(root, "src", "ServerMonitor.Core", "Interfaces", "IMdnsServiceBrowser.cs"),
            Path.Combine(root, "src", "ServerMonitor.Core", "Interfaces", "IServerDiscoveryService.cs"),
            Path.Combine(root, "src", "ServerMonitor.Core", "Interfaces", "IIgnoredDeviceStore.cs"),
            Path.Combine(root, "src", "ServerMonitor.App", "Services", "ServerDiscoveryService.cs"),
            Path.Combine(root, "src", "ServerMonitor.App", "Services", "DiscoveryOptions.cs")
        });

        var source = string.Join(Environment.NewLine, files.Select(File.ReadAllText));
        foreach (var forbidden in ForbiddenCapabilities)
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<Type> GetPublicSurfaceTypes(Type type)
    {
        yield return type;
        foreach (var property in type.GetProperties())
        {
            yield return Unwrap(property.PropertyType);
        }

        foreach (var @event in type.GetEvents())
        {
            yield return Unwrap(@event.EventHandlerType!);
        }

        foreach (var method in type.GetMethods().Where(method => !method.IsSpecialName))
        {
            yield return Unwrap(method.ReturnType);
            foreach (var parameter in method.GetParameters())
            {
                yield return Unwrap(parameter.ParameterType);
            }
        }
    }

    private static Type Unwrap(Type type)
    {
        if (type.IsArray)
        {
            return Unwrap(type.GetElementType()!);
        }

        return type.IsGenericType
            ? type.GetGenericArguments().Select(Unwrap).FirstOrDefault(candidate =>
                candidate.Assembly.GetName().Name?.StartsWith("Tmds", StringComparison.OrdinalIgnoreCase) == true) ?? type
            : type;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate ServerMonitor.slnx from test output.");
    }

    private static string StripComments(string source) =>
        System.Text.RegularExpressions.Regex.Replace(
            source,
            @"/\*[\s\S]*?\*/|//[^\r\n]*",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.None);
}
