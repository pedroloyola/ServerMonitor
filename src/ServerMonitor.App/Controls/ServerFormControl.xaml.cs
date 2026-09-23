using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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

    private async void OnImportSshConfigClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerEditorViewModel viewModel)
        {
            await viewModel.LoadSshConfigHostsAsync();
        }
    }

    private void OnCloseSshConfigImportClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerEditorViewModel viewModel)
        {
            viewModel.CloseSshConfigImport();
        }
    }

    // A ListViewItem's UIA name otherwise falls back to the item's type name; give each container the
    // localized "alias — importable / blocked: why" text (containers are recycled, so set it every time).
    private void OnSshConfigHostContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is SshConfigHostOptionViewModel option)
        {
            AutomationProperties.SetName(args.ItemContainer, option.AccessibleName);
        }
    }

    private void OnUseSshConfigHostClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerEditorViewModel viewModel
            && sender is FrameworkElement { DataContext: SshConfigHostOptionViewModel option })
        {
            viewModel.ApplySshConfigHost(option);
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
