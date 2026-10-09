using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.7C (B-9): the connection-test content of the editor page's in-page modal layer. Drawing only: it shows a
/// <see cref="ConnectionTestView"/> over the view model's four real stages and raises <see cref="CloseRequested"/>
/// (Cancelar teste / Voltar ao formulário / Rever …), <see cref="RetryRequested"/> (Tentar / Testar novamente) and
/// <see cref="PrepHelpRequested"/>; the page decides through its controller. Each stage change is spoken once, as a
/// notification (progress is never only visual).
/// </summary>
public sealed partial class ServerEditorTestPanel : UserControl
{
    private ILocalizationService? _localization;
    private ConnectionChecklistViewModel? _checklist;
    private ConnectionTestPhase _shownPhase;

    public ServerEditorTestPanel()
    {
        InitializeComponent();
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? RetryRequested;

    /// <summary>"Como preparo o meu servidor?" under a failed stage: the page opens the form's helper at this anchor.</summary>
    public event EventHandler<FrameworkElement>? PrepHelpRequested;

    /// <summary>The title, which also names the layer for UI Automation.</summary>
    public string Title => TitleText.Text;

    /// <summary>The phase on screen (None before the first <see cref="Show"/>).</summary>
    public ConnectionTestPhase Phase => _shownPhase;

    public void Configure(ILocalizationService localization) =>
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));

    /// <summary>Draws <paramref name="view"/>; true when the phase changed (the page then moves focus to the safe action).</summary>
    public bool Show(ConnectionTestView view, ServerEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(viewModel);
        var localization = _localization ?? throw new InvalidOperationException("Configure the panel first.");
        Attach(viewModel.ConnectionChecklist);

        TitleIcon.Data = view.IconKey is { } icon ? Application.Current.Resources[icon] as string : null;
        TitleIcon.Visibility = view.ShowsProgress ? Visibility.Collapsed : Visibility.Visible;
        TitleProgress.IsActive = view.ShowsProgress;
        TitleProgress.Visibility = view.ShowsProgress ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = localization.GetString(view.TitleKey);
        var endpoint = viewModel.EndpointDisplay;
        BodyText.Text = view.Phase == ConnectionTestPhase.Testing
            ? string.Format(CultureInfo.CurrentCulture, localization.GetString(view.BodyKey), endpoint)
            : localization.GetString(view.BodyKey);
        SubjectText.Text = Subject(viewModel, localization);
        ToolTipService.SetToolTip(SubjectText, SubjectText.Text);
        AutomationProperties.SetName(StageList, TitleText.Text);

        VerifiedDetailText.Visibility = view.VerifiedDetailKey is null ? Visibility.Collapsed : Visibility.Visible;
        VerifiedDetailText.Text = view.VerifiedDetailKey is { } detail ? localization.GetString(detail) : string.Empty;
        CloseButton.Content = localization.GetString(view.CloseKey);
        RetryButton.Visibility = view.RetryKey is null ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Content = view.RetryKey is { } retry ? localization.GetString(retry) : string.Empty;

        var changed = _shownPhase != view.Phase;
        _shownPhase = view.Phase;
        return changed;
    }

    /// <summary>The safe action takes the focus (Cancelar teste while testing, else back to the form).</summary>
    public bool FocusSafeButton() => CloseButton.Focus(FocusState.Programmatic);

    /// <summary>Final c2 (Prism C2-1): the commands stack (default first, full width) when the dialog is too narrow.</summary>
    public void SetStackedCommands(bool stacked) => DialogCommandRow.Apply(ActionRow, CloseButton, RetryButton, stacked);

    /// <summary>The panel is hidden: it stops listening to the checklist (the next Show attaches again).</summary>
    public void Detach()
    {
        Attach(null);
        _shownPhase = ConnectionTestPhase.None;
    }

    // "prod-web-01 · 192.168.1.10:22 · monitor" (+ " · via bastion.example.com:22" for a routed server).
    private static string Subject(ServerEditorViewModel viewModel, ILocalizationService localization)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(viewModel.Name))
        {
            parts.Add(viewModel.Name.Trim());
        }

        if (viewModel.EndpointDisplay.Length > 0)
        {
            parts.Add(viewModel.EndpointDisplay);
        }

        if (!string.IsNullOrWhiteSpace(viewModel.Username))
        {
            parts.Add(viewModel.Username.Trim());
        }

        if (viewModel.UseJumpHost && !string.IsNullOrWhiteSpace(viewModel.JumpHost))
        {
            parts.Add(string.Format(
                CultureInfo.CurrentCulture,
                localization.GetString("ServerEditorTestViaFormat"),
                viewModel.JumpHost.Trim()));
        }

        return string.Join(" · ", parts);
    }

    private void Attach(ConnectionChecklistViewModel? checklist)
    {
        if (ReferenceEquals(_checklist, checklist))
        {
            return;
        }

        if (_checklist is not null)
        {
            _checklist.PropertyChanged -= OnChecklistPropertyChanged;
        }

        _checklist = checklist;
        StageList.ItemsSource = checklist?.Steps;
        if (checklist is not null)
        {
            checklist.PropertyChanged += OnChecklistPropertyChanged;
        }
    }

    // Each checklist change is spoken once, as a notification on the stage list (B-20: stage semantics, not only visual).
    private void OnChecklistPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectionChecklistViewModel.Announcement)
            || _checklist?.Announcement is not { Length: > 0 } announcement)
        {
            return;
        }

        var peer = FrameworkElementAutomationPeer.FromElement(StageList)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(StageList);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.ImportantMostRecent,
            announcement,
            "ServerEditorConnectionTest");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnRetryClick(object sender, RoutedEventArgs e) => RetryRequested?.Invoke(this, EventArgs.Empty);

    private void OnPrepHelpClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement anchor)
        {
            PrepHelpRequested?.Invoke(this, anchor);
        }
    }

    // Copies the command text and nothing else; the button says so only when the clipboard took it.
    private void OnCopyCommandClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ConnectionStepViewModel { HasCommand: true } step || _localization is null)
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(step.Command);
            Clipboard.SetContent(package);
        }
        catch (Exception)
        {
            // The clipboard can be held by another process; the command stays selectable in place.
            return;
        }

        if (sender is Button button)
        {
            button.Content = _localization.GetString("ServerPrepCopied");
        }
    }
}
