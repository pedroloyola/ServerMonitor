using System.Net;
using ServerMonitor.Core.Discovery;

namespace ServerMonitor.Core.Tests.Discovery;

public sealed class DiscoveryInputPolicyTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NormalSshAnnouncement_IsSanitizedIntoObservation()
    {
        var address = IPAddress.Parse("192.168.10.42");
        var observation = Create(instance: "Mac Studio", type: "_ssh._tcp", domain: "local.",
            host: "Mac-Studio", port: 22, addresses: [address], interfaceId: "  ethernet-1  ");

        Assert.NotNull(observation);
        Assert.Equal("Mac Studio", observation.Identity.InstanceName);
        Assert.Equal("_ssh._tcp", observation.Identity.ServiceType);
        Assert.Equal("local", observation.Identity.Domain);
        Assert.Equal("mac-studio.local", observation.HostName);
        Assert.Equal(22, observation.Port);
        Assert.Equal([address], observation.Addresses);
        Assert.Equal("ethernet-1", observation.InterfaceId);
        Assert.Equal(ObservedAt, observation.ObservedAt);
    }

    [Fact]
    public void IPv4Address_IsPreserved()
    {
        var address = IPAddress.Parse("10.0.0.8");
        var observation = Create(addresses: [address]);

        Assert.Equal(address, Assert.Single(observation!.Addresses));
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, observation.Addresses[0].AddressFamily);
    }

    [Fact]
    public void IPv6LinkLocalAddress_PreservesScopeId()
    {
        var address = IPAddress.Parse("fe80::1234%17");
        var observation = Create(addresses: [address], interfaceId: "17");

        var retained = Assert.Single(observation!.Addresses);
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetworkV6, retained.AddressFamily);
        Assert.Equal(17, retained.ScopeId);
        Assert.Equal("17", observation.InterfaceId);
    }

    [Fact]
    public void Addresses_AreDeduplicatedOrderedAndBounded()
    {
        var addresses = Enumerable.Range(1, DiscoveryInputPolicy.MaxAddressesPerService + 5)
            .Select(index => IPAddress.Parse($"10.0.0.{index}"))
            .ToList();
        addresses.Insert(1, addresses[0]);
        addresses.Insert(2, IPAddress.Any);
        addresses.Insert(3, IPAddress.IPv6Any);

        var observation = Create(addresses: addresses);

        Assert.Equal(DiscoveryInputPolicy.MaxAddressesPerService, observation!.Addresses.Count);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), observation.Addresses[0]);
        Assert.Equal(IPAddress.Parse("10.0.0.16"), observation.Addresses[^1]);
        Assert.Equal(observation.Addresses.Count, observation.Addresses.Distinct().Count());
        Assert.DoesNotContain(IPAddress.Any, observation.Addresses);
        Assert.DoesNotContain(IPAddress.IPv6Any, observation.Addresses);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void InvalidPort_IsRejected(int port) => Assert.Null(Create(port: port));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad host")]
    [InlineData("bad\thost")]
    [InlineData("bad\r\nhost")]
    public void EmptyWhitespaceOrControlHostname_IsRejected(string? host) =>
        Assert.Null(Create(host: host));

    [Fact]
    public void MalformedDnsHostname_IsRejected()
    {
        var malformed = new[]
        {
            "bad..host",
            "bad/host",
            "bad:host",
            "-leading-hyphen",
            "trailing-hyphen-"
        };

        Assert.All(malformed, host => Assert.Null(Create(host: host)));
    }

    [Theory]
    [InlineData(null, "_ssh._tcp")]
    [InlineData("", "_ssh._tcp")]
    [InlineData("   ", "_ssh._tcp")]
    [InlineData("Server", null)]
    [InlineData("Server", "")]
    [InlineData("Server", "   ")]
    public void EmptyInstanceOrServiceType_IsRejected(string? instance, string? type) =>
        Assert.Null(Create(instance: instance, type: type));

    [Fact]
    public void NonSshServiceType_IsRejected() => Assert.Null(Create(type: "_http._tcp"));

    [Theory]
    [InlineData("Server\0Name")]
    [InlineData("Server\rName")]
    [InlineData("Server\nName")]
    public void ControlCharacterInInstance_IsRejected(string instance) =>
        Assert.Null(Create(instance: instance));

    [Fact]
    public void UnicodeInstanceName_IsPreserved()
    {
        var observation = Create(instance: "Servidor São José 🖥️");
        Assert.Equal("Servidor São José 🖥️", observation!.Identity.InstanceName);
    }

    [Fact]
    public void BidiOverrideInInstance_IsRejected() =>
        Assert.Null(Create(instance: "safe\u202Etxt.exe"));

    [Fact]
    public void InstanceLengthLimit_IsEnforcedAtExactBoundary()
    {
        Assert.NotNull(Create(instance: new string('a', DiscoveryInputPolicy.MaxInstanceNameLength)));
        Assert.Null(Create(instance: new string('a', DiscoveryInputPolicy.MaxInstanceNameLength + 1)));
    }

    [Fact]
    public void ComposedHostnameLengthLimit_IsEnforcedAtExactBoundary()
    {
        var exact = string.Join('.',
            new string('a', 63),
            new string('b', 63),
            new string('c', 63),
            new string('d', 57),
            "local");

        Assert.Equal(DiscoveryInputPolicy.MaxHostNameLength, Create(host: exact)!.HostName.Length);
        Assert.Null(Create(host: exact.Replace(new string('d', 57), new string('d', 58))));
    }

    [Fact]
    public void DnsLabelLengthLimit_IsEnforcedAtExactBoundary()
    {
        Assert.NotNull(Create(host: new string('a', DiscoveryInputPolicy.MaxDnsLabelLength)));
        Assert.Null(Create(host: new string('a', DiscoveryInputPolicy.MaxDnsLabelLength + 1)));
    }

    [Fact]
    public void CaseAndTrailingDots_AreCanonicalized()
    {
        var observation = Create(instance: "  My Server.  ", type: "._SSH._TCP.",
            domain: ".LOCAL.", host: "EXAMPLE.LOCAL.");

        Assert.NotNull(observation);
        Assert.Equal("My Server", observation.Identity.InstanceName);
        Assert.Equal("_ssh._tcp", observation.Identity.ServiceType);
        Assert.Equal("local", observation.Identity.Domain);
        Assert.Equal("example.local", observation.HostName);
    }

    [Fact]
    public void EmptyInterfaceId_NormalizesToDefault() =>
        Assert.Equal("default", Create(interfaceId: "  ")!.InterfaceId);

    private static DiscoveryObservation? Create(
        string? instance = "Example SSH",
        string? type = "_ssh._tcp",
        string? domain = "local.",
        string? host = "example",
        int port = 22,
        IEnumerable<IPAddress>? addresses = null,
        string? interfaceId = "nic-a") =>
        DiscoveryInputPolicy.TryCreateObservation(instance, type, domain, host, port,
            addresses ?? [IPAddress.Parse("192.168.1.20")], interfaceId, ObservedAt);
}
