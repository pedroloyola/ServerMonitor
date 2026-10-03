using System.Globalization;
using System.Windows.Input;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.ViewModels;

public sealed class HiddenServerItemViewModel
{
    public HiddenServerItemViewModel(
        Server server,
        ILocalizationService localizationService,
        Func<Task> restore,
        int positionInSet = 0,
        int sizeOfSet = 0)
    {
        Server = server;
        PositionInSet = positionInSet;
        SizeOfSet = sizeOfSet;
        OperatingSystemDisplayName = localizationService.GetString(
            $"OperatingSystem{server.OperatingSystem}");
        RestoreAutomationName = string.Format(
            CultureInfo.CurrentUICulture,
            localizationService.GetString("HiddenServerRestoreFor"),
            server.Name);
        RestoreCommand = new AsyncRelayCommand(restore);
    }

    public Server Server { get; }

    public string Name => Server.Name;

    public string Endpoint => $"{Server.Host}:{Server.Port}";

    public string OperatingSystemDisplayName { get; }

    public string RestoreAutomationName { get; }

    public ICommand RestoreCommand { get; }

    /// <summary>UI.5 Beacon C1 M2: "1 of 3" for the item's Restaurar (the list is a list for UI Automation too).</summary>
    public int PositionInSet { get; }

    public int SizeOfSet { get; }
}
