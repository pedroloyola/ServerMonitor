using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.7B (B-10): the trust content of the editor page's in-page modal layer. Drawing only: it shows a
/// <see cref="HostKeyTrustPrompt"/> and raises <see cref="AcceptRequested"/> / <see cref="CloseRequested"/>; the page
/// decides through its controller. A mismatch has no accept button at all; the first focus is the safe button.
/// </summary>
public sealed partial class ServerEditorTrustPanel : UserControl
{
    private ILocalizationService? _localization;

    public ServerEditorTrustPanel()
    {
        InitializeComponent();
    }

    public event EventHandler? AcceptRequested;

    public event EventHandler? CloseRequested;

    /// <summary>The prompt on screen (null before the first <see cref="ShowPrompt"/>).</summary>
    public HostKeyTrustPrompt? Prompt { get; private set; }

    /// <summary>The title, which also names the layer for UI Automation.</summary>
    public string Title => TitleText.Text;

    public void Configure(ILocalizationService localization) =>
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));

    public void ShowPrompt(HostKeyTrustPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var localization = _localization ?? throw new InvalidOperationException("Configure the panel first.");
        Prompt = prompt;
        StepText.Visibility = prompt.Step is null ? Visibility.Collapsed : Visibility.Visible;
        StepText.Text = prompt.Step is { } step
            ? string.Format(CultureInfo.CurrentCulture, localization.GetString("ServerEditorTrustStepFormat"), step, 2)
            : string.Empty;
        TitleIcon.Data = Application.Current.Resources[prompt.CanAccept ? "SaIconKey01Data" : "SaIconAlert02Data"] as string;
        TitleText.Text = localization.GetString(prompt.TitleKey);
        BodyText.Text = localization.GetString(prompt.BodyKey);
        SubjectText.Text = prompt.Subject;
        ToolTipService.SetToolTip(SubjectText, prompt.Subject);
        ScopeText.Text = localization.GetString(prompt.ScopeKey);
        PresentedLabel.Text = string.Format(
            CultureInfo.CurrentCulture,
            localization.GetString(prompt.CanAccept ? "ServerEditorTrustPresentedFormat" : "ServerEditorTrustReceivedFormat"),
            prompt.Algorithm);
        PresentedFingerprintText.Text = prompt.Fingerprint;
        TrustedBlock.Visibility = prompt.CanAccept || prompt.TrustedFingerprint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        TrustedLabel.Text = localization.GetString("ServerEditorTrustSavedLabel");
        TrustedFingerprintText.Text = prompt.TrustedFingerprint;

        // A mismatch is never accepted: there is no accept button, only "Voltar ao formulário".
        AcceptButton.Visibility = prompt.AcceptKey is null ? Visibility.Collapsed : Visibility.Visible;
        AcceptButton.Content = prompt.AcceptKey is { } accept ? localization.GetString(accept) : string.Empty;
        CloseButton.Content = localization.GetString(prompt.CloseKey);
        SetWorking(false, acceptAllowed: true);
    }

    /// <summary>
    /// The accepted key is being written and the SAME retest runs (only Cancelar remains, which cancels that test), or any
    /// connection work is running: "Confiar e …" is disabled.
    /// </summary>
    public void SetWorking(bool working, bool acceptAllowed)
    {
        AcceptButton.IsEnabled = !working && acceptAllowed;
        WorkingRow.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        WorkingRing.IsActive = working;
        WorkingText.Text = working && _localization is { } localization ? localization.GetString("ServerEditorTrustWorking") : string.Empty;
    }

    /// <summary>The first focus is the safe button: a stray Enter never trusts a key.</summary>
    public void FocusSafeButton() => CloseButton.Focus(FocusState.Programmatic);

    private void OnAcceptClick(object sender, RoutedEventArgs e) => AcceptRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
