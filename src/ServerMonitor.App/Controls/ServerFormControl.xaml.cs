using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Controls;

public sealed partial class ServerFormControl : UserControl
{
    public ServerFormControl()
    {
        InitializeComponent();
    }

    public void FocusFirstField() => NameField.Focus(FocusState.Programmatic);

    public void CaptureSecret()
    {
        if (DataContext is not ServerEditorViewModel viewModel)
        {
            return;
        }

        var secret = viewModel.IsPasswordAuthentication
            ? PasswordField.Password
            : PassphraseField.Password;
        viewModel.CaptureSecret(secret);
        PasswordField.Password = string.Empty;
        PassphraseField.Password = string.Empty;
    }

    private async void OnChoosePrivateKeyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerEditorViewModel viewModel)
        {
            await viewModel.SelectPrivateKeyAsync();
        }
    }

    private async void OnTestConnectionClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerEditorViewModel viewModel)
        {
            CaptureSecret();
            await viewModel.TestConnectionAsync();
            if (viewModel.HasUnknownHostKey)
            {
                UnknownHostHeading.Focus(FocusState.Programmatic);
            }
        }
    }

    private void OnCancelTestClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerEditorViewModel viewModel)
        {
            viewModel.CancelTest();
        }
    }

    private async void OnTrustAndConnectClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerEditorViewModel viewModel)
        {
            await viewModel.TrustAndConnectAsync();
        }
    }

    private void OnDismissHostKeyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerEditorViewModel viewModel)
        {
            viewModel.DismissHostKeyPrompt();
        }
    }
}
