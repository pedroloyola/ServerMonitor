using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using ServerMonitor.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace ServerMonitor.App.Controls;

public sealed partial class ServerFormControl : UserControl
{
    private ServerEditorViewModel? _viewModel;
    // True while code (not the user) moves a key selector's selection.
    private bool _syncingKeySelectors;
    private ServerPrepCommands? _prepCommands;
    private string _copyLabel = string.Empty;

    public ServerFormControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as ServerEditorViewModel);
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

        // The jump host's secret is independent of the target's and staged separately.
        var jumpSecret = viewModel.IsJumpPasswordAuthentication
            ? JumpPasswordField.Password
            : JumpPassphraseField.Password;
        viewModel.CaptureJumpSecret(jumpSecret);
        JumpPasswordField.Password = string.Empty;
        JumpPassphraseField.Password = string.Empty;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        Attach(args.NewValue as ServerEditorViewModel);

    private void Attach(ServerEditorViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ConnectionChecklist.PropertyChanged -= OnChecklistPropertyChanged;
        }

        _viewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.ConnectionChecklist.PropertyChanged += OnChecklistPropertyChanged;
            SyncKeySelectors();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerEditorViewModel.SelectedLocalKeyOption)
            or nameof(ServerEditorViewModel.SelectedJumpLocalKeyOption))
        {
            SyncKeySelectors();
        }
    }

    // The selectors mirror the key paths: a found key shows as selected, any other file (or none) shows the
    // placeholder. Done here rather than by a binding so a programmatic change is never mistaken for a choice.
    private void SyncKeySelectors()
    {
        if (_viewModel is null)
        {
            return;
        }

        _syncingKeySelectors = true;
        try
        {
            LocalKeySelector.SelectedItem = _viewModel.SelectedLocalKeyOption;
            JumpLocalKeySelector.SelectedItem = _viewModel.SelectedJumpLocalKeyOption;
        }
        finally
        {
            _syncingKeySelectors = false;
        }
    }

    private async void OnLocalKeySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingKeySelectors
            || DataContext is not ServerEditorViewModel viewModel
            || LocalKeySelector.SelectedItem is not LocalKeyOptionViewModel option
            // SelectionChanged can be raised after SyncKeySelectors has returned: a selection that only
            // mirrors the view model is never a choice, or the pre-selected key would stop being "found".
            || ReferenceEquals(option, viewModel.SelectedLocalKeyOption))
        {
            return;
        }

        if (option.IsBrowse)
        {
            // The existing picker; on cancel the view model asks the selector to snap back.
            await viewModel.SelectPrivateKeyAsync();
        }
        else
        {
            viewModel.SelectLocalKey(option);
        }
    }

    private async void OnJumpLocalKeySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingKeySelectors
            || DataContext is not ServerEditorViewModel viewModel
            || JumpLocalKeySelector.SelectedItem is not LocalKeyOptionViewModel option
            || ReferenceEquals(option, viewModel.SelectedJumpLocalKeyOption))
        {
            return;
        }

        if (option.IsBrowse)
        {
            await viewModel.SelectJumpPrivateKeyAsync();
        }
        else
        {
            viewModel.SelectJumpLocalKey(option);
        }
    }

    // Each checklist change is spoken once, as a notification on the checklist itself.
    private void OnChecklistPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectionChecklistViewModel.Announcement)
            || _viewModel is not { } viewModel
            || viewModel.ConnectionChecklist.Announcement is not { Length: > 0 } announcement)
        {
            return;
        }

        var peer = FrameworkElementAutomationPeer.FromElement(ChecklistPanel)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(ChecklistPanel);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.ImportantMostRecent,
            announcement,
            "ServerFormConnectionChecklist");
    }

    private void OnPrepHelpClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement anchor && FlyoutBase.GetAttachedFlyout(PrepHelpLink) is { } flyout)
        {
            flyout.ShowAt(anchor);
        }
    }

    private void OnPrepHelpOpening(object? sender, object e)
    {
        if (DataContext is not ServerEditorViewModel viewModel)
        {
            return;
        }

        // A flyout is its own popup: give it the modal's theme, and rebuild the commands from the form as it is now.
        PrepHelpContent.RequestedTheme = ActualTheme;
        _prepCommands = viewModel.BuildServerPrepCommands();
        PrepKeygenText.Text = _prepCommands.GenerateKey;
        PrepCopyKeyText.Text = _prepCommands.CopyPublicKey;
        PrepPlaceholderNote.Visibility = _prepCommands.UsesPlaceholders ? Visibility.Visible : Visibility.Collapsed;
        if (_copyLabel.Length == 0)
        {
            _copyLabel = PrepCopyKeygenButton.Content as string ?? string.Empty;
        }

        PrepCopyKeygenButton.Content = _copyLabel;
        PrepCopyKeyButton.Content = _copyLabel;
    }

    private void OnCopyPrepKeygenClick(object sender, RoutedEventArgs e) =>
        CopyCommand(_prepCommands?.GenerateKey, sender);

    private void OnCopyPrepKeyClick(object sender, RoutedEventArgs e) =>
        CopyCommand(_prepCommands?.CopyPublicKey, sender);

    private void OnCopyStepCommandClick(object sender, RoutedEventArgs e) =>
        CopyCommand((sender as FrameworkElement)?.DataContext is ConnectionStepViewModel step ? step.Command : null, sender);

    // Copies the command text and nothing else. The button says so only when the clipboard took it.
    private void CopyCommand(string? command, object sender)
    {
        if (string.IsNullOrEmpty(command))
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(command);
            Clipboard.SetContent(package);
        }
        catch (Exception)
        {
            // The clipboard can be held by another process; the command stays selectable in place.
            return;
        }

        if (sender is Button button && DataContext is ServerEditorViewModel viewModel)
        {
            button.Content = viewModel.CopiedLabel;
        }
    }

    private async void OnChoosePrivateKeyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerEditorViewModel viewModel)
        {
            await viewModel.SelectPrivateKeyAsync();
        }
    }

    private async void OnChooseJumpPrivateKeyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerEditorViewModel viewModel)
        {
            await viewModel.SelectJumpPrivateKeyAsync();
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
