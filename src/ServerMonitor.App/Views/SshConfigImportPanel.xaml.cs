using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.7B (B-11): the "Importar de SSH" content of the editor page's in-page modal layer. Drawing only, from the view
/// model's existing load state; the page wires "Usar perfil" to the controller (the existing Add-only apply) and closing
/// to the load's cancellation.
/// </summary>
public sealed partial class SshConfigImportPanel : UserControl
{
    private ILocalizationService? _localization;

    public SshConfigImportPanel()
    {
        InitializeComponent();
    }

    public event EventHandler? UseRequested;

    /// <summary>UI.7C (H-UI7-2): whether a profile describes a saved server (the page's controller answers).</summary>
    public Func<Core.SshConfig.SshConfigHostEntry, bool>? IsAlreadyAdded { get; set; }

    public event EventHandler? CloseRequested;

    /// <summary>The title, which also names the layer for UI Automation.</summary>
    public string Title => TitleText.Text;

    /// <summary>The selected profile (blocked ones can be selected to read why, never used).</summary>
    public SshConfigHostOptionViewModel? Selected => HostList.SelectedItem as SshConfigHostOptionViewModel;

    public void Configure(ILocalizationService localization) =>
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));

    /// <summary>Redraws the state from the view model (loading, a 07 state, or the profile list).</summary>
    public void Update(ServerEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        var localization = _localization ?? throw new InvalidOperationException("Configure the panel first.");
        var loading = viewModel.IsLoadingSshConfig;
        LoadingRow.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = loading;

        var stateTitleKey = SshConfigImportPresentation.StateTitleKey(viewModel.SshConfigLoadOutcome);
        var showState = !loading && stateTitleKey.Length > 0;
        StatePanel.Visibility = showState ? Visibility.Visible : Visibility.Collapsed;
        if (showState)
        {
            StateTitle.Text = localization.GetString(stateTitleKey);
            StateBody.Text = viewModel.SshConfigStatusMessage;
            StateIcon.Data = Application.Current.Resources[
                viewModel.SshConfigLoadOutcome == SshConfigLoadOutcome.NoHosts ? "SaIconInformationCircleData" : "SaIconAlert02Data"] as string;
        }

        var hosts = viewModel.SshConfigHosts;
        ListPanel.Visibility = !loading && hosts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(HostList.ItemsSource, hosts))
        {
            HostList.ItemsSource = hosts;
        }

        CountText.Text = SshConfigImportPresentation.CountText(
            SshConfigImportPresentation.Available(hosts),
            SshConfigImportPresentation.Blocked(hosts),
            localization);
        WarningText.Text = viewModel.SshConfigFileWarningMessage;
        WarningText.Visibility = !loading && viewModel.HasSshConfigFileWarning ? Visibility.Visible : Visibility.Collapsed;
        UseButton.IsEnabled = SshConfigImportPresentation.CanUse(Selected);
        UpdateDetails();
    }

    /// <summary>UI.7C: the saved-server list arrived after the rows were drawn: redraw them so "Já adicionado" is current.</summary>
    public void RefreshMarkers()
    {
        if (HostList.ItemsSource is { } hosts)
        {
            var selected = HostList.SelectedItem;
            HostList.ItemsSource = null;
            HostList.ItemsSource = hosts;
            HostList.SelectedItem = selected;
        }
    }

    /// <summary>The first focus: the list when it has profiles, otherwise Cancelar.</summary>
    public void FocusInitial()
    {
        if (ListPanel.Visibility == Visibility.Visible)
        {
            HostList.Focus(FocusState.Programmatic);
        }
        else
        {
            CancelButton.Focus(FocusState.Programmatic);
        }
    }

    private void OnHostSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UseButton.IsEnabled = SshConfigImportPresentation.CanUse(Selected);
        UpdateDetails();
    }

    // Final c1 (Prism F-2, Figma 04): the selected profile's Host / Utilizador / Chave privada. Only what the file gives;
    // the key as its file name, the full path in the tooltip and the UIA HelpText.
    private void UpdateDetails()
    {
        if (_localization is not { } localization || Selected is not { } selected || ListPanel.Visibility != Visibility.Visible)
        {
            DetailsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var entry = selected.Entry;
        var notSet = localization.GetString("ServerEditorImportDetailsNotSet");
        DetailsHostValue.Text = string.IsNullOrWhiteSpace(entry.HostName) ? notSet : entry.HostName;
        DetailsUserValue.Text = string.IsNullOrWhiteSpace(entry.User) ? notSet : entry.User;
        var keyPath = entry.IdentityFile?.Trim() ?? string.Empty;
        DetailsKeyValue.Text = ServerEditorKeyPicker.FileName(keyPath) ?? notSet;
        ToolTipService.SetToolTip(DetailsKeyValue, keyPath.Length == 0 ? null : keyPath);
        AutomationProperties.SetName(DetailsHostValue, DetailsHostLabel.Text + " " + DetailsHostValue.Text);
        AutomationProperties.SetName(DetailsUserValue, DetailsUserLabel.Text + " " + DetailsUserValue.Text);
        AutomationProperties.SetName(DetailsKeyValue, DetailsKeyLabel.Text + " " + DetailsKeyValue.Text);
        AutomationProperties.SetHelpText(DetailsKeyValue, keyPath);
        DetailsPanel.Visibility = Visibility.Visible;
    }

    // A ListViewItem's UIA name otherwise falls back to the item's type: give each container the localized
    // "alias — importable / blocked: why" text (containers are recycled, so set it every time).
    private void OnHostContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is SshConfigHostOptionViewModel option)
        {
            var added = IsAlreadyAdded?.Invoke(option.Entry) == true;
            if (args.ItemContainer.ContentTemplateRoot is FrameworkElement root)
            {
                if (root.FindName("AlreadyAddedText") is TextBlock marker)
                {
                    marker.Visibility = added ? Visibility.Visible : Visibility.Collapsed;
                }

                if (_localization is { } rowLocalization)
                {
                    SetLine(root.FindName("DetailText") as TextBlock, SshConfigImportPresentation.Detail(option.Entry, rowLocalization));
                    SetLine(root.FindName("JumpText") as TextBlock, SshConfigImportPresentation.Jump(option.Entry, rowLocalization));
                }
            }

            // The key's full path is never drawn in the row: it is the item's UIA HelpText (and the details block's tooltip).
            AutomationProperties.SetHelpText(args.ItemContainer, option.Entry.IdentityFile?.Trim() ?? string.Empty);

            AutomationProperties.SetName(
                args.ItemContainer,
                added && _localization is { } localization
                    ? string.Format(CultureInfo.CurrentCulture, localization.GetString("ServerEditorImportAlreadyAddedAccessibleFormat"), option.AccessibleName)
                    : option.AccessibleName);
        }
    }

    private static void SetLine(TextBlock? line, string text)
    {
        if (line is not null)
        {
            line.Text = text;
            line.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnUseClick(object sender, RoutedEventArgs e) => UseRequested?.Invoke(this, EventArgs.Empty);

    private void OnCancelClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
