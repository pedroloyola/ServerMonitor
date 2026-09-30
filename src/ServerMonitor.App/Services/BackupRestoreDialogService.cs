using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.Core.Backup;

namespace ServerMonitor.App.Services;

/// <summary>The WinUI surfaces behind <see cref="IBackupRestoreInteraction"/>. Layout only.</summary>
public sealed class BackupRestoreDialogService(IWindowContext windowContext) : IBackupRestoreInteraction
{
    public async Task ShowCreateDialogAsync(BackupCreateSession session)
    {
        var dialog = new BackupCreateDialog(session);
        ConfigureDialog(dialog);
        await dialog.ShowAsync();
    }

    public async Task ShowRestoreOpenDialogAsync(RestoreOpenSession session)
    {
        var dialog = new RestoreOpenDialog(session);
        ConfigureDialog(dialog);
        await dialog.ShowAsync();
    }

    public async Task<bool> ConfirmRestoreAsync(RestoreConfirmation confirmation)
    {
        var dialog = new RestoreConfirmDialog(confirmation);
        ConfigureDialog(dialog);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task<RestoreApplyResult> ShowRestoringAsync(string message, Func<Task<RestoreApplyResult>> apply)
    {
        var content = new StackPanel { Spacing = 12, MinWidth = 320 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new ProgressBar { IsIndeterminate = true });
        var dialog = CreateDialog(content);

        // No button, and Esc is refused: the surface leaves only when the restore has an outcome.
        var finished = false;
        dialog.Closing += (_, args) => args.Cancel = !finished;
        var shown = dialog.ShowAsync().AsTask();
        try
        {
            return await apply();
        }
        finally
        {
            finished = true;
            dialog.Hide();
            try
            {
                await shown;
            }
            catch (Exception)
            {
                // The surface is only a progress indication; its own failure changes nothing.
            }
        }
    }

    public async Task ShowRestoreCompletedAsync(string title, string message, string primaryText)
    {
        var dialog = CreateDialog(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        dialog.Title = title;
        dialog.PrimaryButtonText = primaryText;
        dialog.DefaultButton = ContentDialogButton.Primary;
        await dialog.ShowAsync();
    }

    public async Task ShowNoticeAsync(string title, string message, string closeText)
    {
        var dialog = CreateDialog(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            MaxWidth = 480
        });
        dialog.Title = title;
        dialog.CloseButtonText = closeText;
        dialog.DefaultButton = ContentDialogButton.Close;
        await dialog.ShowAsync();
    }

    private ContentDialog CreateDialog(UIElement content)
    {
        var dialog = new ContentDialog
        {
            Content = content,
            Style = (Style)Application.Current.Resources["PremiumContentDialogStyle"]
        };
        ConfigureDialog(dialog);
        return dialog;
    }

    // Same sizing contract as ServerDialogService: PremiumContentDialogStyle stretches over the window.
    private void ConfigureDialog(ContentDialog dialog)
    {
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
