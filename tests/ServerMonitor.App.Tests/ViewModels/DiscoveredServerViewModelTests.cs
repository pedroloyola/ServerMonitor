using System.Net;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.ViewModels;

public sealed class DiscoveredServerViewModelTests
{
    [Fact]
    public void HostnameService_ProducesSafeExactPrefill()
    {
        var vm = Create(Service("Example SSH", "example.local", 22, ["192.168.1.8"]));

        var prefill = vm.ToPrefill();

        Assert.Equal("Example SSH", prefill.Name);
        Assert.Equal("example.local", prefill.Host);
        Assert.Equal(22, prefill.Port);
        Assert.Equal("example.local:22", vm.Endpoint);
        Assert.Equal(3, typeof(ServerDiscoveryPrefill).GetProperties().Length);
    }

    [Fact]
    public void ScopedIPv6Endpoint_IsBracketedForDisplayButBareInPrefill()
    {
        var address = IPAddress.Parse("fe80::42%7");
        var vm = Create(Service("IPv6 SSH", string.Empty, 2222, [address.ToString()]));

        Assert.Equal(address.ToString(), vm.PrimaryHost);
        Assert.Equal($"[{address}]:2222", vm.Endpoint);
        Assert.Equal(address.ToString(), vm.ToPrefill().Host);
    }

    [Fact]
    public void PrefillContract_CannotCarrySecurityOrMonitoringState()
    {
        var names = typeof(ServerDiscoveryPrefill).GetProperties().Select(property => property.Name).ToArray();

        Assert.Equal(["Name", "Host", "Port"], names);
        Assert.DoesNotContain(names, name => name.Contains("Username", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Authentication", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Fingerprint", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("OperatingSystem", StringComparison.OrdinalIgnoreCase));
    }

    private static DiscoveredServerViewModel Create(DiscoveredService service) => new(
        service,
        new FakeLocalizationService(),
        _ => Task.CompletedTask,
        _ => Task.CompletedTask);

    internal static DiscoveredService Service(
        string instance,
        string host,
        int port,
        IReadOnlyList<string>? addresses = null)
    {
        var identity = ServiceInstanceIdentity.TryCreate(instance, "_ssh._tcp", "local")!;
        var now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        return new DiscoveredService
        {
            DiscoveryId = Guid.NewGuid(),
            Source = DiscoverySource.Mdns,
            Identity = identity,
            DisplayName = instance,
            HostName = host,
            Port = port,
            Addresses = (addresses ?? []).Select(IPAddress.Parse).ToArray(),
            FirstSeenAt = now,
            LastSeenAt = now
        };
    }
}
