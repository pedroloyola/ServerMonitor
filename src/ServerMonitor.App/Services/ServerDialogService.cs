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
    IRoutedHostKeyTrustStore routedHostKeyTrustStore,
    IServerConnectionStateStore connectionStateStore,
    IPrivateKeyFilePicker privateKeyFilePicker,
    ILocalizationService localizationService,
    ISshConfigImportSource sshConfigImportSource,
    ILocalSshKeyDiscovery localSshKeyDiscovery) : IServerDialogService
{
    public Task<ServerEditorResult?> ShowEditorAsync(Server? server) =>
        ShowEditorCoreAsync(server, prefill: null, isEdit: server is not null);

    public Task<ServerEditorResult?> ShowEditorForDiscoveryAsync(ServerDiscoveryPrefill prefill) =>
        ShowEditorCoreAsync(server: null, prefill: prefill, isEdit: false);

    public Task<ServerEditorResult?> ShowEditorForSshImportAsync() =>
        ShowEditorCoreAsync(server: null, prefill: null, isEdit: false, openSshConfigImport: true);

    private async Task<ServerEditorResult?> ShowEditorCoreAsync(
        Server? server,
        ServerDiscoveryPrefill? prefill,
        bool isEdit,
        bool openSshConfigImport = false)
    {
        var viewModel = new ServerEditorViewModel(
            validator,
            sshConnectionService,
            hostKeyTrustStore,
            connectionStateStore,
            privateKeyFilePicker,
            localizationService,
            server,
            prefill,
            sshConfigImportSource,
            routedHostKeyTrustStore,
            localSshKeyDiscovery);

        try
        {
            if (openSshConfigImport)
            {
                // Same read-only load as the form's import button; it reports its own failures in the panel.
                _ = viewModel.LoadSshConfigHostsAsync();
            }

            return await ServerEditorModal.ShowAsync(
                windowContext,
                viewModel,
                localizationService,
                isEdit);
        }
        finally
        {
            viewModel.Dispose();
        }
    }

    public async Task<bool> ConfirmRemoveAsync(Server server)
    {
        // UI.5 fix round 2 (Prism C1 M-3): the Figma section-11 confirmation (every caller: Visão geral, Servidores, Detail).
        // Kind=Destructive keeps the M14/UI.4 semantics: what is removed is named, Cancelar is the default and first focus.
        if (windowContext.XamlRoot is null)
        {
            return false;
        }

        var dialog = new DestructiveConfirmDialog(DestructiveConfirmation.RemoveServer(
            server, localizationService, DestructiveConfirmation.IconData("SaIconServerStack01Data")))
        {
            XamlRoot = windowContext.XamlRoot,
            RequestedTheme = windowContext.ActualTheme
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
