using System.Net;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Qa;

// QA-ONLY. Excluded from Release builds (see ServerMonitor.App.csproj) and only wired into DI under
// the --qa-discovery flag. It lets the real dashboard "Encontrados na rede" section and the real
// DiscoveredServerCard be inspected against a deterministic set of suggestions with no mDNS, no
// network and no SSH. Nothing here is shipped.

/// <summary>
/// Two deterministic discovery suggestions — enough to exercise the section, an Ignore that drops
/// it to one then zero, and a Reset that brings them back. Purely in-memory; carries no trust,
/// credential or metric surface, exactly like a real <see cref="DiscoveredService"/>.
/// </summary>
internal static class QaDiscoveryCatalog
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public static IReadOnlyList<DiscoveredService> Seed() =>
    [
        Build(
            new Guid("11111111-1111-1111-1111-111111111111"),
            instanceName: "Mac Studio",
            hostName: "mac-studio.local",
            port: 22,
            address: "192.168.1.42",
            order: 0),
        Build(
            new Guid("22222222-2222-2222-2222-222222222222"),
            instanceName: "Raspberry Pi",
            hostName: "raspberrypi.local",
            port: 22,
            address: "192.168.1.77",
            order: 1),
    ];

    private static DiscoveredService Build(
        Guid discoveryId,
        string instanceName,
        string hostName,
        int port,
        string address,
        int order)
    {
        var identity = ServiceInstanceIdentity.TryCreate(instanceName, "_ssh._tcp", "local")
            ?? throw new InvalidOperationException("QA discovery seed identity must be valid.");

        return new DiscoveredService
        {
            DiscoveryId = discoveryId,
            Source = DiscoverySource.Mdns,
            Identity = identity,
            DisplayName = instanceName,
            HostName = hostName,
            Port = port,
            Addresses = [IPAddress.Parse(address)],
            FirstSeenAt = Now.AddSeconds(order),
            LastSeenAt = Now.AddSeconds(order),
        };
    }
}
