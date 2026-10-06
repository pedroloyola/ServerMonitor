using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Views;

namespace ServerMonitor.App.Services;

/// <summary>
/// UI.7 H-UI7-3: the "Descartar alterações?" dialog (Figma 112:9018 / 112:9203) - the shared Section 11 destructive
/// confirmation, so Esc, Enter and the first focus stay on "Continuar a editar". No window yet: nothing is discarded.
/// </summary>
public sealed class ServerEditorDiscardPrompt(IWindowContext windowContext, ILocalizationService localizationService)
    : IServerEditorDiscardPrompt
{
    public async Task<bool> ConfirmDiscardAsync(ServerEditorDiscardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var dialog = new DestructiveConfirmDialog(DestructiveConfirmation.DiscardEditorChanges(
            context, localizationService, DestructiveConfirmation.IconData("SaIconServerStack01Data")))
        {
            XamlRoot = windowContext.XamlRoot,
            RequestedTheme = windowContext.ActualTheme
        };
        dialog.FillWindow();
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
