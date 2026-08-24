using System.Globalization;
using System.Windows.Input;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.ViewModels;

public sealed class ServerCardViewModel
{
    public ServerCardViewModel(
        Server server,
        SshConnectionResult? connectionResult,
        ILocalizationService localizationService,
        Func<Task> edit,
        Func<Task> hide,
        Func<Task> remove)
    {
        Server = server;
        OperatingSystemDisplayName = localizationService.GetString(
            $"OperatingSystem{server.OperatingSystem}");
        ConnectionState = connectionResult?.State ?? ServerConnectionState.NeverConnected;
        ConnectionStateDisplayName = localizationService.GetString($"ConnectionState{ConnectionState}");
        MoreOptionsAutomationName = string.Format(
            CultureInfo.CurrentUICulture,
            localizationService.GetString("ServerCardMoreOptionsFor"),
            server.Name);
        AutomationSummary = string.Format(
            CultureInfo.CurrentUICulture,
            localizationService.GetString("ServerCardAutomationSummary"),
            server.Name,
            OperatingSystemDisplayName,
            Endpoint,
            ConnectionStateDisplayName);
        EditCommand = new AsyncRelayCommand(edit);
        HideCommand = new AsyncRelayCommand(hide);
        RemoveCommand = new AsyncRelayCommand(remove);
    }

    public Server Server { get; }

    public string Name => Server.Name;

    public string Host => Server.Host;

    public int Port => Server.Port;

    public string Endpoint => $"{Host}:{Port}";

    public string OperatingSystemDisplayName { get; }

    public string ConnectionStateDisplayName { get; }

    public ServerConnectionState ConnectionState { get; }

    public string MoreOptionsAutomationName { get; }

    public string AutomationSummary { get; }

    public ICommand EditCommand { get; }

    public ICommand HideCommand { get; }

    public ICommand RemoveCommand { get; }
}
