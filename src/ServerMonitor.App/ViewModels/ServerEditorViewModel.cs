using System.Globalization;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.ViewModels;

public sealed class ServerEditorViewModel : ObservableObject
{
    private readonly IServerValidator _validator;
    private string _name;
    private string _host;
    private string _port;
    private string _username;
    private int _selectedOperatingSystemIndex;
    private bool _hasValidationErrors;

    public ServerEditorViewModel(IServerValidator validator, Server? server)
    {
        _validator = validator;
        _name = server?.Name ?? string.Empty;
        _host = server?.Host ?? string.Empty;
        _port = (server?.Port ?? 22).ToString(CultureInfo.InvariantCulture);
        _username = server?.Username ?? string.Empty;
        _selectedOperatingSystemIndex = (int)(server?.OperatingSystem ?? ServerOperatingSystem.Auto);
    }

    public string Name
    {
        get => _name;
        set => SetEditorProperty(ref _name, value);
    }

    public string Host
    {
        get => _host;
        set => SetEditorProperty(ref _host, value);
    }

    public string Port
    {
        get => _port;
        set => SetEditorProperty(ref _port, value);
    }

    public string Username
    {
        get => _username;
        set => SetEditorProperty(ref _username, value);
    }

    public int SelectedOperatingSystemIndex
    {
        get => _selectedOperatingSystemIndex;
        set => SetEditorProperty(ref _selectedOperatingSystemIndex, value);
    }

    public bool HasValidationErrors
    {
        get => _hasValidationErrors;
        set => SetProperty(ref _hasValidationErrors, value);
    }

    public bool TryCreateInput(out ServerInput? input)
    {
        input = null;
        if (!int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort))
        {
            HasValidationErrors = true;
            return false;
        }

        var operatingSystem = Enum.IsDefined((ServerOperatingSystem)SelectedOperatingSystemIndex)
            ? (ServerOperatingSystem)SelectedOperatingSystemIndex
            : ServerOperatingSystem.Unknown;

        var candidate = new ServerInput
        {
            Name = Name,
            Host = Host,
            Port = parsedPort,
            Username = Username,
            OperatingSystem = operatingSystem
        };

        var validation = _validator.Validate(candidate);
        HasValidationErrors = !validation.IsValid;
        if (!validation.IsValid)
        {
            return false;
        }

        input = candidate;
        return true;
    }

    private void SetEditorProperty<T>(
        ref T storage,
        T value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref storage, value, propertyName) && HasValidationErrors)
        {
            HasValidationErrors = false;
        }
    }
}
