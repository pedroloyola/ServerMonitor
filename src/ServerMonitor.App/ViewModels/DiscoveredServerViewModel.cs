using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Windows.Input;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Discovery;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// One row in the dashboard's "Encontrados na rede" section. It presents an untrusted mDNS
/// suggestion — display name, the SSH protocol, a primary endpoint and port — and offers exactly
/// two actions: add it (through the normal editor) or ignore it. It never shows an operating
/// system, health or metrics: a discovery is a suggestion, not a monitored server.
/// </summary>
public sealed class DiscoveredServerViewModel
{
    private readonly ILocalizationService _localizationService;

    public DiscoveredServerViewModel(
        DiscoveredService discovered,
        ILocalizationService localizationService,
        Func<DiscoveredServerViewModel, Task> add,
        Func<DiscoveredServerViewModel, Task> ignore)
    {
        Discovered = discovered;
        _localizationService = localizationService;
        AddCommand = new AsyncRelayCommand(() => add(this));
        IgnoreCommand = new AsyncRelayCommand(() => ignore(this));
    }

    public DiscoveredService Discovered { get; }

    public string DisplayName => Discovered.DisplayName;

    /// <summary>Bare host or address used to seed the editor's Host field (no port, no brackets).</summary>
    public string PrimaryHost => ResolvePrimaryHost(Discovered);

    public int Port => Discovered.Port;

    /// <summary>"host:port" for display; IPv6 is bracketed so the port stays unambiguous.</summary>
    public string Endpoint => FormatEndpoint(PrimaryHost, Discovered.Port);

    public string AddAutomationName => Format("DiscoveredServerAddFor", DisplayName);

    public string IgnoreAutomationName => Format("DiscoveredServerIgnoreFor", DisplayName);

    public string AutomationSummary => Format("DiscoveredServerAutomationSummary", DisplayName, Endpoint);

    public ICommand AddCommand { get; }

    public ICommand IgnoreCommand { get; }

    public ServerDiscoveryPrefill ToPrefill() => new()
    {
        Name = DisplayName,
        Host = PrimaryHost,
        Port = Discovered.Port
    };

    private static string ResolvePrimaryHost(DiscoveredService discovered)
    {
        if (!string.IsNullOrWhiteSpace(discovered.HostName))
        {
            return discovered.HostName;
        }

        var address = discovered.Addresses.FirstOrDefault();
        return address?.ToString() ?? string.Empty;
    }

    private static string FormatEndpoint(string host, int port)
    {
        var needsBrackets = IPAddress.TryParse(host, out var address)
            && address.AddressFamily == AddressFamily.InterNetworkV6;
        var display = needsBrackets ? $"[{host}]" : host;
        return $"{display}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    private string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentUICulture, _localizationService.GetString(key), arguments);
}
