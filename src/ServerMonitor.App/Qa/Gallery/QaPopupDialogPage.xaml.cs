using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Qa.Gallery;

/// <summary>QA-ONLY (UI.2 S6): shows a ContentDialog with SaDialogStyle on load so it can be captured.</summary>
public sealed partial class QaPopupDialogPage : Page
{
    public QaPopupDialogPage()
    {
        InitializeComponent();
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = ShowAsync());
    }

    private void OnOpenDialog(object sender, RoutedEventArgs e) => _ = ShowAsync();

    private async Task ShowAsync()
    {
        Application.Current.Resources.TryGetValue("SaDialogStyle", out var dialogStyle);
        Application.Current.Resources.TryGetValue("SaDestructiveButtonStyle", out var destructiveStyle);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Style = dialogStyle as Style,
            Title = "Remover servidor?",
            Content = "O QA · Healthy · Linux deixa de ser monitorizado. O histórico local é apagado.",
            PrimaryButtonText = "Remover servidor",
            CloseButtonText = "Cancelar",
            PrimaryButtonStyle = destructiveStyle as Style,
            // The Fluent template paints the DEFAULT button with AccentButtonStyle (legacy blue), overriding the Sa button
            // style: Sa dialogs keep DefaultButton = None (documented limitation until UI.6 retires the global accent).
            DefaultButton = ContentDialogButton.None,
            RequestedTheme = ActualTheme
        };
        await dialog.ShowAsync();
    }
}
