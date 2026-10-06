using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Views;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Services;

/// <summary>
/// The server dialogs that are still dialogs. UI.7 B-3: the editor moved to a page owned by
/// <see cref="IServerEditorSession"/>; only the Section 11 remove confirmation stays here.
/// </summary>
public sealed class ServerDialogService(
    IWindowContext windowContext,
    ILocalizationService localizationService) : IServerDialogService
{
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
        dialog.FillWindow();
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
