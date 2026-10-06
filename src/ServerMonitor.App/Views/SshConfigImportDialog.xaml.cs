using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.7B (B-11): the "Importar de SSH" dialog. Drawing only, from the view model's existing load state; the page wires
/// "Usar perfil" to the controller (the existing Add-only apply) and closing to the load's cancellation.
/// </summary>
public sealed partial class SshConfigImportDialog : ContentDialog
{
    private readonly ILocalizationService _localization;

    public SshConfigImportDialog(ILocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        InitializeComponent();
        Title = localization.GetString("ServerEditorImportTitle");
        AutomationProperties.SetName(this, (string)Title);
        PrimaryButtonText = localization.GetString("ServerEditorImportUse");
        CloseButtonText = localization.GetString("ServerEditorImportCancel");
        IsPrimaryButtonEnabled = false;
    }

    /// <summary>The selected profile (blocked ones can be selected to read why, never used).</summary>
    public SshConfigHostOptionViewModel? Selected => HostList.SelectedItem as SshConfigHostOptionViewModel;

    /// <summary>Redraws the state from the view model (loading, a 07 state, or the profile list).</summary>
    public void Update(ServerEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        var loading = viewModel.IsLoadingSshConfig;
        LoadingRow.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = loading;

        var stateTitleKey = SshConfigImportPresentation.StateTitleKey(viewModel.SshConfigLoadOutcome);
        var showState = !loading && stateTitleKey.Length > 0;
        StatePanel.Visibility = showState ? Visibility.Visible : Visibility.Collapsed;
        if (showState)
        {
            StateTitle.Text = _localization.GetString(stateTitleKey);
            StateBody.Text = viewModel.SshConfigStatusMessage;
            StateIcon.Data = Application.Current.Resources[
                viewModel.SshConfigLoadOutcome == SshConfigLoadOutcome.NoHosts ? "SaIconInformationCircleData" : "SaIconAlert02Data"] as string;
        }

        var hosts = viewModel.SshConfigHosts;
        var showList = !loading && hosts.Count > 0;
        ListPanel.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(HostList.ItemsSource, hosts))
        {
            HostList.ItemsSource = hosts;
        }

        CountText.Text = string.Format(
            CultureInfo.CurrentCulture,
            _localization.GetString("ServerEditorImportCountFormat"),
            SshConfigImportPresentation.Available(hosts),
            SshConfigImportPresentation.Blocked(hosts));
        WarningText.Text = viewModel.SshConfigFileWarningMessage;
        WarningText.Visibility = !loading && viewModel.HasSshConfigFileWarning ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = SshConfigImportPresentation.CanUse(Selected);
    }

    /// <summary>The smoke layer covers the whole window (like the other Sa dialogs). Call after XamlRoot.</summary>
    public void FillWindow()
    {
        if (XamlRoot is not { } root)
        {
            return;
        }

        void UpdateBounds()
        {
            Width = root.Size.Width;
            Height = root.Size.Height;
        }

        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateBounds();
        UpdateBounds();
        root.Changed += OnRootChanged;
        Closed += (_, _) => root.Changed -= OnRootChanged;
    }

    private void OnHostSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        IsPrimaryButtonEnabled = SshConfigImportPresentation.CanUse(Selected);

    // A ListViewItem's UIA name otherwise falls back to the item's type: give each container the localized
    // "alias — importable / blocked: why" text (containers are recycled, so set it every time).
    private void OnHostContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is SshConfigHostOptionViewModel option)
        {
            AutomationProperties.SetName(args.ItemContainer, option.AccessibleName);
        }
    }
}
