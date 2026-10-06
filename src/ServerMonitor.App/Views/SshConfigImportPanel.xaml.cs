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

        CountText.Text = string.Format(
            CultureInfo.CurrentCulture,
            localization.GetString("ServerEditorImportCountFormat"),
            SshConfigImportPresentation.Available(hosts),
            SshConfigImportPresentation.Blocked(hosts));
        WarningText.Text = viewModel.SshConfigFileWarningMessage;
        WarningText.Visibility = !loading && viewModel.HasSshConfigFileWarning ? Visibility.Visible : Visibility.Collapsed;
        UseButton.IsEnabled = SshConfigImportPresentation.CanUse(Selected);
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

    private void OnHostSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UseButton.IsEnabled = SshConfigImportPresentation.CanUse(Selected);

    // A ListViewItem's UIA name otherwise falls back to the item's type: give each container the localized
    // "alias — importable / blocked: why" text (containers are recycled, so set it every time).
    private void OnHostContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is SshConfigHostOptionViewModel option)
        {
            var added = IsAlreadyAdded?.Invoke(option.Entry) == true;
            if (args.ItemContainer.ContentTemplateRoot is FrameworkElement root && root.FindName("AlreadyAddedText") is TextBlock marker)
            {
                marker.Visibility = added ? Visibility.Visible : Visibility.Collapsed;
            }

            AutomationProperties.SetName(
                args.ItemContainer,
                added && _localization is { } localization
                    ? string.Format(CultureInfo.CurrentCulture, localization.GetString("ServerEditorImportAlreadyAddedAccessibleFormat"), option.AccessibleName)
                    : option.AccessibleName);
        }
    }

    private void OnUseClick(object sender, RoutedEventArgs e) => UseRequested?.Invoke(this, EventArgs.Empty);

    private void OnCancelClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
