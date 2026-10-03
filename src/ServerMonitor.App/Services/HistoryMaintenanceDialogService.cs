using ServerMonitor.App.Views;

namespace ServerMonitor.App.Services;

/// <summary>
/// The view half of history maintenance: the two destructive confirmations. UI.5 fix round 2 (Prism C1 M-3): the Figma
/// section-11 dialog (<see cref="DestructiveConfirmDialog"/>, SaDialogStyle + Kind=Destructive) with the affected-data
/// line; the semantics are unchanged from M10 - the safe Cancelar is the default and takes the first focus, so Enter never
/// deletes. Without a window there is nothing to confirm with: false.
/// </summary>
public sealed class HistoryMaintenanceDialogService(
    IWindowContext windowContext,
    ILocalizationService localizationService) : IHistoryMaintenanceInteraction
{
    public Task<bool> ConfirmClearHistoryAsync() =>
        ConfirmAsync(DestructiveConfirmation.ClearHistory(localizationService, DestructiveConfirmation.IconData("SaIconClock01Data")));

    public Task<bool> ConfirmResetHistoryAsync() =>
        ConfirmAsync(DestructiveConfirmation.ResetHistory(localizationService, DestructiveConfirmation.IconData("SaIconClock01Data")));

    private async Task<bool> ConfirmAsync(DestructiveConfirmation confirmation)
    {
        if (windowContext.XamlRoot is null)
        {
            return false;
        }

        var dialog = new DestructiveConfirmDialog(confirmation)
        {
            XamlRoot = windowContext.XamlRoot,
            RequestedTheme = windowContext.ActualTheme
        };
        dialog.FillWindow();

        return await dialog.ShowAsync() == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary;
    }
}
