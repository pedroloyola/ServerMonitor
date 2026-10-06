using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace ServerMonitor.App.Controls;

public sealed partial class ServerFormControl : UserControl
{
    private ServerEditorViewModel? _viewModel;
    private ServerPrepCommands? _prepCommands;
    private string _copyLabel = string.Empty;
    private ILocalizationService? _localization;
    private bool _isEditMode;
    private IReadOnlyDictionary<ServerEditorField, string> _errors = new Dictionary<ServerEditorField, string>();

    public ServerFormControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as ServerEditorViewModel);
        PasswordField.PasswordChanged += OnPasswordChanged;
        PassphraseField.PasswordChanged += OnPasswordChanged;
        JumpPasswordField.PasswordChanged += OnPasswordChanged;
        JumpPassphraseField.PasswordChanged += OnPasswordChanged;
    }

    /// <summary>A password box gained or lost typed text (the page's hint and the password errors follow it).</summary>
    public event EventHandler? TypedSecretChanged;

    public void FocusFirstField() => NameField.Focus(FocusState.Programmatic);

    /// <summary>B-16: the first invalid field takes the focus after a failed attempt (its error is described by it).</summary>
    public void FocusField(ServerEditorField field)
    {
        Control target = field switch
        {
            ServerEditorField.Name => NameField,
            ServerEditorField.Host => HostField,
            ServerEditorField.Username => UsernameField,
            ServerEditorField.PrivateKey => KeyPickerButton,
            ServerEditorField.Password => PasswordField,
            ServerEditorField.Port => PortField,
            ServerEditorField.JumpHost => JumpHostField,
            ServerEditorField.JumpUsername => JumpUsernameField,
            ServerEditorField.JumpPort => JumpPortField,
            ServerEditorField.JumpPrivateKey => JumpKeyPickerButton,
            _ => JumpPasswordField
        };
        target.StartBringIntoView();
        target.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// UI.7C (B-16, Figma 06): each field shows its own message under the input with the 1.5 danger border (SaFormField
    /// describes the input with it). A password box with typed text hides its "missing" error - the view model only sees
    /// the secret on the next Test/Save.
    /// </summary>
    public void ShowErrors(IReadOnlyDictionary<ServerEditorField, string> errors)
    {
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
        ApplyErrors();
    }

    /// <summary>"Como preparo o meu servidor?" from a failed stage of the test dialog: the same helper, at that anchor.</summary>
    public void ShowPrepHelp(FrameworkElement anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (FlyoutBase.GetAttachedFlyout(PrepHelpLink) is { } flyout)
        {
            flyout.ShowAt(anchor);
        }
    }

    private void ApplyErrors()
    {
        if (_localization is not { } localization)
        {
            return;
        }

        SetError(NameFormField, NameField, ServerEditorField.Name);
        SetError(HostFormField, HostField, ServerEditorField.Host);
        SetError(UsernameFormField, UsernameField, ServerEditorField.Username);
        SetError(PortFormField, PortField, ServerEditorField.Port);
        SetError(JumpHostFormField, JumpHostField, ServerEditorField.JumpHost);
        SetError(JumpUsernameFormField, JumpUsernameField, ServerEditorField.JumpUsername);
        SetError(JumpPortFormField, JumpPortField, ServerEditorField.JumpPort);
        SetError(PrivateKeyField, KeyPickerButton, ServerEditorField.PrivateKey);
        SetError(JumpPrivateKeyField, JumpKeyPickerButton, ServerEditorField.JumpPrivateKey);
        SetError(PasswordFormField, PasswordField, ServerEditorField.Password, typed: PasswordField.Password.Length > 0);
        SetError(JumpPasswordFormField, JumpPasswordField, ServerEditorField.JumpPassword, typed: JumpPasswordField.Password.Length > 0);

        void SetError(Primitives.SaFormField field, Control input, ServerEditorField key, bool typed = false)
        {
            var message = !typed && _errors.TryGetValue(key, out var messageKey) ? localization.GetString(messageKey) : string.Empty;
            field.ErrorText = message;
            var invalid = message.Length > 0;
            input.Style = (Style)Application.Current.Resources[input switch
            {
                TextBox => invalid ? "SaTextFieldErrorStyle" : "SaTextFieldStyle",
                PasswordBox => invalid ? "SaPasswordBoxErrorStyle" : "SaPasswordBoxStyle",
                _ => invalid ? "SaPickerButtonErrorStyle" : "SaPickerButtonStyle"
            }];
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        ApplyErrors();
        TypedSecretChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// UI.7A: the mode-dependent card copy (Figma 04 Add / 05 Edit). Set once by the editor page before the form loads.
    /// </summary>
    public void Configure(ILocalizationService localization, bool isEditMode)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _isEditMode = isEditMode;
        IdentitySubtitle.Text = localization.GetString(isEditMode ? "ServerEditorIdentitySubtitleEdit" : "ServerEditorIdentitySubtitleAdd");
        AuthTitle.Text = localization.GetString(isEditMode ? "ServerEditorAuthTitleEdit" : "ServerEditorAuthTitleAdd");
        UpdateAuthSubtitle();
        UpdatePresentation();
    }

    /// <summary>A password box holds text the view model has not received yet (it only reads them on Test/Save).</summary>
    public bool HasTypedSecret() =>
        PasswordField.Password.Length > 0
        || PassphraseField.Password.Length > 0
        || JumpPasswordField.Password.Length > 0
        || JumpPassphraseField.Password.Length > 0;

    /// <summary>R-2 / Vigil H5: the page clears every password box when it unloads or leaves.</summary>
    public void ClearSecrets()
    {
        PasswordField.Password = string.Empty;
        PassphraseField.Password = string.Empty;
        JumpPasswordField.Password = string.Empty;
        JumpPassphraseField.Password = string.Empty;
    }

    /// <summary>
    /// "Testar ligação" (in the page's action bar): stage the typed secrets (read, then cleared), then test. A key the
    /// test meets is shown by the page's trust dialog; nothing here accepts it.
    /// </summary>
    public async Task TestConnectionAsync()
    {
        if (DataContext is ServerEditorViewModel viewModel && !viewModel.IsConnectionWorkInProgress)
        {
            CaptureSecret();
            await viewModel.TestConnectionAsync();
        }
    }

    private void UpdateAuthSubtitle()
    {
        if (_localization is null || _viewModel is not { } viewModel)
        {
            return;
        }

        var key = (_isEditMode, viewModel.IsPasswordAuthentication) switch
        {
            (false, false) => "ServerEditorAuthSubtitleKeyAdd",
            (false, true) => "ServerEditorAuthSubtitlePasswordAdd",
            (true, false) => "ServerEditorAuthSubtitleKeyEdit",
            (true, true) => viewModel.HasSavedPassword ? "ServerEditorAuthSubtitlePasswordKeepEdit" : "ServerEditorAuthSubtitlePasswordEdit"
        };
        AuthSubtitle.Text = _localization.GetString(key);
    }

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
        }

        _viewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateAuthSubtitle();
            UpdatePresentation();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerEditorViewModel.SelectedAuthenticationIndex))
        {
            UpdateAuthSubtitle();
        }

        if (e.PropertyName is nameof(ServerEditorViewModel.PrivateKeyPath)
            or nameof(ServerEditorViewModel.JumpPrivateKeyPath)
            or nameof(ServerEditorViewModel.PrivateKeyHint)
            or nameof(ServerEditorViewModel.HasSavedPassphrase)
            or nameof(ServerEditorViewModel.HasSavedPassword)
            or nameof(ServerEditorViewModel.HasSavedJumpSecret)
            or nameof(ServerEditorViewModel.SelectedAuthenticationIndex)
            or nameof(ServerEditorViewModel.SelectedJumpAuthenticationIndex)
            or nameof(ServerEditorViewModel.UseJumpHost)
            or nameof(ServerEditorViewModel.JumpHost)
            or nameof(ServerEditorViewModel.Host)
            or nameof(ServerEditorViewModel.SshConfigStatusMessage)
            or nameof(ServerEditorViewModel.IsSshConfigImportOpen))
        {
            UpdatePresentation();
        }
    }

    // UI.7B: everything derived from the view model that a binding cannot say on its own - the key pickers (file name,
    // full path only in tooltip/HelpText, the auto-selection hint), the saved-secret labels (G-13: never dots), the
    // route line and the import status. Text only; never a secret (the password boxes are never read here).
    private void UpdatePresentation()
    {
        if (_localization is not { } localization || _viewModel is not { } viewModel)
        {
            return;
        }

        UpdateKeyPicker(
            KeyPickerButton,
            KeyPickerText,
            PrivateKeyField,
            viewModel.PrivateKeyPath,
            viewModel.HasPrivateKeyHint ? viewModel.PrivateKeyHint : localization.GetString("ServerEditorKeyPickerHelper"),
            PrivateKeyField.Header);
        // B-20: the jump picker is named apart from the target's (same visible label, different UIA name).
        UpdateKeyPicker(
            JumpKeyPickerButton,
            JumpKeyPickerText,
            JumpPrivateKeyField,
            viewModel.JumpPrivateKeyPath,
            localization.GetString("ServerEditorJumpKeyPickerHelper"),
            localization.GetString("ServerEditorJumpKeyPickerAccessibleHeader"));

        SetSavedSecretLabels(PassphraseFormField, PassphraseField, viewModel.HasSavedPassphrase,
            "ServerEditorPassphraseHeader", "ServerEditorPassphrasePlaceholder", "ServerEditorPassphraseSavedHeader");
        SetSavedSecretLabels(PasswordFormField, PasswordField, viewModel.HasSavedPassword,
            "ServerEditorPasswordHeader", "ServerEditorPasswordPlaceholder", "ServerEditorPasswordSavedHeader");
        SetSavedSecretLabels(JumpPassphraseFormField, JumpPassphraseField, viewModel.HasSavedJumpSecret,
            "ServerEditorPassphraseHeader", "ServerEditorJumpPassphrasePlaceholder", "ServerEditorPassphraseSavedHeader");
        SetSavedSecretLabels(JumpPasswordFormField, JumpPasswordField, viewModel.HasSavedJumpSecret,
            "ServerEditorPasswordHeader", "ServerEditorJumpPasswordPlaceholder", "ServerEditorPasswordSavedHeader");

        RouteLine.Text = ServerEditorRouteLine.Describe(viewModel.JumpHost, viewModel.Host, localization);
        ToolTipService.SetToolTip(RouteLine, RouteLine.Text);
        ImportStatusText.Visibility = viewModel.HasSshConfigStatus && !viewModel.IsSshConfigImportOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateKeyPicker(Button button, TextBlock text, Primitives.SaFormField field, string path, string helper, string accessibleHeader)
    {
        var localization = _localization!;
        var label = ServerEditorKeyPicker.ButtonText(path, localization);
        text.Text = label;
        field.HelperText = helper;
        var fullPath = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim();
        ToolTipService.SetToolTip(button, fullPath.Length == 0 ? null : fullPath);
        AutomationProperties.SetName(button, string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            localization.GetString("ServerEditorKeyPickerAccessibleFormat"),
            accessibleHeader,
            label));
        AutomationProperties.SetHelpText(button, fullPath);
    }

    // A saved secret is never shown: the box stays empty, the label says "guardada" and the placeholder says how to keep it.
    private void SetSavedSecretLabels(
        Primitives.SaFormField field,
        PasswordBox box,
        bool saved,
        string headerKey,
        string placeholderKey,
        string savedHeaderKey)
    {
        var localization = _localization!;
        field.Header = localization.GetString(saved ? savedHeaderKey : headerKey);
        box.PlaceholderText = localization.GetString(saved ? "ServerEditorSecretKeepPlaceholder" : placeholderKey);
    }

    private void OnKeyPickerMenuOpening(object? sender, object e) =>
        FillKeyMenu(KeyPickerMenu, jump: false);

    private void OnJumpKeyPickerMenuOpening(object? sender, object e) =>
        FillKeyMenu(JumpKeyPickerMenu, jump: true);

    // The menu is rebuilt each time it opens: the keys discovery found (metadata only) with the current one checked, then
    // the browse entry. Choosing goes through the SAME view model calls as before (SelectLocalKey / the file picker).
    private void FillKeyMenu(MenuFlyout menu, bool jump)
    {
        menu.Items.Clear();
        if (_viewModel is not { } viewModel || _localization is null)
        {
            return;
        }

        var current = jump ? viewModel.SelectedJumpLocalKeyOption : viewModel.SelectedLocalKeyOption;
        foreach (var option in ServerEditorKeyPicker.MenuItems(viewModel.LocalKeyOptions, _localization))
        {
            if (option.IsBrowse)
            {
                if (menu.Items.Count > 0)
                {
                    menu.Items.Add(new MenuFlyoutSeparator());
                }

                var browse = new MenuFlyoutItem { Text = option.Label };
                browse.Click += async (_, _) =>
                {
                    if (jump)
                    {
                        await viewModel.SelectJumpPrivateKeyAsync();
                    }
                    else
                    {
                        await viewModel.SelectPrivateKeyAsync();
                    }
                };
                menu.Items.Add(browse);
                continue;
            }

            var key = new RadioMenuFlyoutItem
            {
                Text = option.Label,
                GroupName = jump ? "JumpKeyChoices" : "KeyChoices",
                IsChecked = ReferenceEquals(option, current)
            };
            key.Click += (_, _) =>
            {
                if (jump)
                {
                    viewModel.SelectJumpLocalKey(option);
                }
                else
                {
                    viewModel.SelectLocalKey(option);
                }
            };
            menu.Items.Add(key);
        }
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

        // A flyout is its own popup: give it the page's theme, and rebuild the commands from the form as it is now.
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
}
