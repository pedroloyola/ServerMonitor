using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Services;

public sealed class ServerDialogService(
    IWindowContext windowContext,
    IServerValidator validator,
    ISshConnectionService sshConnectionService,
    IHostKeyTrustStore hostKeyTrustStore,
    IServerConnectionStateStore connectionStateStore,
    IPrivateKeyFilePicker privateKeyFilePicker,
    ILocalizationService localizationService) : IServerDialogService
{
    public async Task<ServerEditorResult?> ShowEditorAsync(Server? server)
    {
        var viewModel = new ServerEditorViewModel(
            validator,
            sshConnectionService,
            hostKeyTrustStore,
            connectionStateStore,
            privateKeyFilePicker,
            localizationService,
            server);
        if (server is null)
        {
            var dialog = new AddServerDialog(viewModel) { XamlRoot = windowContext.XamlRoot };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                return dialog.Result;
            }

            viewModel.Dispose();
            return null;
        }

        var editDialog = new EditServerDialog(viewModel) { XamlRoot = windowContext.XamlRoot };
        if (await editDialog.ShowAsync() == ContentDialogResult.Primary)
        {
            return editDialog.Result;
        }

        viewModel.Dispose();
        return null;
    }

    public async Task<bool> ConfirmRemoveAsync(Server server)
    {
        var dialog = new RemoveServerDialog(server.Name) { XamlRoot = windowContext.XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
