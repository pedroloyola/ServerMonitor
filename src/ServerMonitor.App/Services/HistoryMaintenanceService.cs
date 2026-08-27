using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.Core.History;

namespace ServerMonitor.App.Services;

/// <summary>
/// Clears local history after an explicit, destructive-styled confirmation (spec §33). Touches only
/// the history database; it also resets the recorder's per-server cadence so the next cycle records
/// immediately. Never deletes servers, credentials, known hosts, ignored devices or settings.
/// </summary>
public sealed class HistoryMaintenanceService(
    IServerHistoryStore store,
    HistoryWriterService writer,
    HistoryRecorder recorder,
    IWindowContext windowContext,
    ILocalizationService localizationService) : IHistoryMaintenanceService
{
    public bool IsAvailable => store.IsAvailable;

    public async Task<HistoryClearOutcome> ClearHistoryWithConfirmationAsync()
    {
        if (windowContext.XamlRoot is null)
        {
            return HistoryClearOutcome.Cancelled;
        }

        var dialog = new ContentDialog
        {
            Title = localizationService.GetString("HistoryClearConfirmTitle"),
            Content = localizationService.GetString("HistoryClearConfirmMessage"),
            PrimaryButtonText = localizationService.GetString("HistoryClearConfirmPrimary"),
            CloseButtonText = localizationService.GetString("HistoryClearConfirmClose"),
            DefaultButton = ContentDialogButton.Close, // safe default for a destructive action
            XamlRoot = windowContext.XamlRoot,
            RequestedTheme = windowContext.ActualTheme
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return HistoryClearOutcome.Cancelled;
        }

        if (!store.IsAvailable)
        {
            return HistoryClearOutcome.Unavailable;
        }

        if (!await writer.ClearAsync().ConfigureAwait(true))
        {
            return HistoryClearOutcome.Unavailable;
        }

        recorder.ForgetAll();
        return HistoryClearOutcome.Cleared;
    }

    public async Task<HistoryResetOutcome> ResetHistoryWithConfirmationAsync()
    {
        if (windowContext.XamlRoot is null)
        {
            return HistoryResetOutcome.Cancelled;
        }

        var dialog = new ContentDialog
        {
            Title = localizationService.GetString("HistoryResetConfirmTitle"),
            Content = localizationService.GetString("HistoryResetConfirmMessage"),
            PrimaryButtonText = localizationService.GetString("HistoryResetConfirmPrimary"),
            CloseButtonText = localizationService.GetString("HistoryClearConfirmClose"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = windowContext.XamlRoot,
            RequestedTheme = windowContext.ActualTheme
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return HistoryResetOutcome.Cancelled;
        }

        if (!await writer.ResetAsync().ConfigureAwait(true))
        {
            return HistoryResetOutcome.Unavailable;
        }

        recorder.ForgetAll();
        return HistoryResetOutcome.Reset;
    }
}

/// <summary>Registered by default so Settings resolves even when the real history stack is absent
/// (QA harnesses): reports unavailable and clears nothing.</summary>
public sealed class NullHistoryMaintenanceService : IHistoryMaintenanceService
{
    public bool IsAvailable => false;

    public Task<HistoryClearOutcome> ClearHistoryWithConfirmationAsync() =>
        Task.FromResult(HistoryClearOutcome.Unavailable);

    public Task<HistoryResetOutcome> ResetHistoryWithConfirmationAsync() =>
        Task.FromResult(HistoryResetOutcome.Unavailable);
}
