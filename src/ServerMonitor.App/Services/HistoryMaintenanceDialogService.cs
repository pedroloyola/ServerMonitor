using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Services;

/// <summary>
/// The view half of history maintenance: the two destructive-styled confirmations, unchanged from M10 (the safe Close
/// button is the default, so Enter never deletes). Without a window there is nothing to confirm with: false.
/// </summary>
public sealed class HistoryMaintenanceDialogService(
    IWindowContext windowContext,
    ILocalizationService localizationService) : IHistoryMaintenanceInteraction
{
    public Task<bool> ConfirmClearHistoryAsync() =>
        ConfirmAsync("HistoryClearConfirmTitle", "HistoryClearConfirmMessage", "HistoryClearConfirmPrimary");

    public Task<bool> ConfirmResetHistoryAsync() =>
        ConfirmAsync("HistoryResetConfirmTitle", "HistoryResetConfirmMessage", "HistoryResetConfirmPrimary");

    private async Task<bool> ConfirmAsync(string titleKey, string messageKey, string primaryKey)
    {
        if (windowContext.XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = localizationService.GetString(titleKey),
            Content = localizationService.GetString(messageKey),
            PrimaryButtonText = localizationService.GetString(primaryKey),
            CloseButtonText = localizationService.GetString("HistoryClearConfirmClose"),
            DefaultButton = ContentDialogButton.Close, // safe default for a destructive action
            XamlRoot = windowContext.XamlRoot,
            RequestedTheme = windowContext.ActualTheme
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
