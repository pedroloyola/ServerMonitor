using System.ComponentModel;
using ServerMonitor.App.ViewModels;
using Windows.System;
namespace ServerMonitor.App.Services;

/// <summary>Reapplies the router projection even after native RadioButton changes its local value.</summary>
internal sealed class SidebarSelectionSync(Action<ShellDestination, bool> apply) : IDisposable
{
    private ShellViewModel? _shell;
    public void Bind(ShellViewModel? shell)
    {
        if (_shell is not null) _shell.PropertyChanged -= OnChanged;
        _shell = shell;
        if (_shell is not null) _shell.PropertyChanged += OnChanged;
        Refresh();
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args) => Refresh();
    public void Refresh()
    {
        foreach (var destination in Enum.GetValues<ShellDestination>())
            apply(destination, _shell?.SelectedDestination == destination);
    }
    public bool ActivateKey(VirtualKey key, ShellDestination destination)
    {
        if (key != VirtualKey.Enter || _shell is null) return false;
        _shell.Navigate(destination);
        Refresh();
        return true;
    }
    public void Dispose() => Bind(null);
}
