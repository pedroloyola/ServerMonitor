using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Services;

public sealed class ServerDialogService(
    IWindowContext windowContext,
    IServerValidator validator) : IServerDialogService
{
    public async Task<ServerInput?> ShowEditorAsync(Server? server)
    {
        var viewModel = new ServerEditorViewModel(validator, server);
        if (server is null)
        {
            var dialog = new AddServerDialog(viewModel) { XamlRoot = windowContext.XamlRoot };
            return await dialog.ShowAsync() == ContentDialogResult.Primary
                ? dialog.ResultInput
                : null;
        }

        var editDialog = new EditServerDialog(viewModel) { XamlRoot = windowContext.XamlRoot };
        return await editDialog.ShowAsync() == ContentDialogResult.Primary
            ? editDialog.ResultInput
            : null;
    }

    public async Task<bool> ConfirmRemoveAsync(Server server)
    {
        var dialog = new RemoveServerDialog(server.Name) { XamlRoot = windowContext.XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
