using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Features;
using ServerMonitor.Infrastructure.Discovery;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.App.Features;

/// <summary>
/// M7 passive local network discovery (mDNS/DNS-SD, <c>_ssh._tcp</c> only). One instance backs the
/// <see cref="IServerDiscoveryService"/> facade and the hosted-service lifecycle. The Tmds.MDns adapter is
/// the fakeable Found/Updated/Removed seam; ignored decisions live in their own non-sensitive file,
/// separate from <c>servers.json</c>.
/// <para>
/// <b>Two registrations, one lifetime.</b> The concrete <see cref="ServerDiscoveryService"/> is also an
/// <c>IHostedService</c>, and that hosted-service registration deliberately stays in the composition root
/// next to the other three: hosted-service ORDER is a documented behavioural contract there (reverse-order
/// shutdown), and moving one into a module would reorder it against tray, notifications and alerts. The
/// two registrations are governed by the same condition as before, so they remain all-or-nothing.
/// </para>
/// </summary>
public sealed class DiscoveryFeatureModule : IFeatureModule
{
    public FeatureDescriptor Descriptor => CommunityFeatures.Discovery;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton(IgnoredDeviceStorageOptions.ForCurrentUser());
        services.AddSingleton(MdnsServiceBrowserOptions.Default);
        services.AddSingleton<IIgnoredDeviceStore, JsonIgnoredDeviceStore>();
        services.AddSingleton<IMdnsServiceBrowser>(sp => new TmdsMdnsServiceBrowser(
            sp.GetRequiredService<ILogger<TmdsMdnsServiceBrowser>>(),
            sp.GetRequiredService<MdnsServiceBrowserOptions>()));
        services.AddSingleton(sp => new ServerDiscoveryService(
            sp.GetRequiredService<IMdnsServiceBrowser>(),
            sp.GetRequiredService<IIgnoredDeviceStore>(),
            sp.GetRequiredService<ILogger<ServerDiscoveryService>>()));
        services.AddSingleton<IServerDiscoveryService>(sp => sp.GetRequiredService<ServerDiscoveryService>());
    }
}
