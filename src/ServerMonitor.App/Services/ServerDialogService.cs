using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Controls;
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

        try
        {
            return await ServerEditorModal.ShowAsync(
                windowContext,
                viewModel,
                localizationService,
                isEdit: server is not null);
        }
        finally
        {
            viewModel.Dispose();
        }
    }

    public async Task<bool> ConfirmRemoveAsync(Server server)
    {
        var dialog = new RemoveServerDialog(server.Name);
        ConfigureDialog(dialog);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void ConfigureDialog(ContentDialog dialog)
    {
        if (windowContext.XamlRoot is null)
        {
            return;
        }

        dialog.XamlRoot = windowContext.XamlRoot;
        dialog.RequestedTheme = windowContext.ActualTheme;

        void UpdateBounds()
        {
            if (dialog.XamlRoot is not null)
            {
                dialog.Width = dialog.XamlRoot.Size.Width;
                dialog.Height = dialog.XamlRoot.Size.Height;
            }
        }

        UpdateBounds();

        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateBounds();
        dialog.XamlRoot.Changed += OnRootChanged;
        dialog.Closed += (_, _) =>
        {
            if (dialog.XamlRoot is not null)
            {
                dialog.XamlRoot.Changed -= OnRootChanged;
            }
        };
    }
}
