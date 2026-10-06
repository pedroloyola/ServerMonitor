using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.7B (B-10): the editor's single connection dialog host. Drawing only: it shows a <see cref="HostKeyTrustPrompt"/>
/// (and, from 7C, the connection test). The page decides what the buttons do through its controller. The safe button is
/// the default and takes the first focus, so Enter never trusts a key by accident; a mismatch has no accept button.
/// </summary>
public sealed partial class ServerEditorConnectionDialog : ContentDialog
{
    private readonly ILocalizationService _localization;

    public ServerEditorConnectionDialog(ILocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        InitializeComponent();
        DefaultButton = ContentDialogButton.Close;
        Opened += (_, _) => FocusSafeButton();
    }

    /// <summary>The prompt on screen (null before the first <see cref="ShowPrompt"/>).</summary>
    public HostKeyTrustPrompt? Prompt { get; private set; }

    public void ShowPrompt(HostKeyTrustPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        Prompt = prompt;
        StepText.Visibility = prompt.Step is null ? Visibility.Collapsed : Visibility.Visible;
        StepText.Text = prompt.Step is { } step
            ? string.Format(CultureInfo.CurrentCulture, _localization.GetString("ServerEditorTrustStepFormat"), step, 2)
            : string.Empty;
        TitleIcon.Data = Application.Current.Resources[prompt.CanAccept ? "SaIconKey01Data" : "SaIconAlert02Data"] as string;
        TitleText.Text = _localization.GetString(prompt.TitleKey);
        AutomationProperties.SetName(this, TitleText.Text);
        BodyText.Text = _localization.GetString(prompt.BodyKey);
        SubjectText.Text = prompt.Subject;
        ToolTipService.SetToolTip(SubjectText, prompt.Subject);
        ScopeText.Text = _localization.GetString(prompt.ScopeKey);

        var algorithm = prompt.Algorithm.Length == 0 ? string.Empty : prompt.Algorithm;
        PresentedLabel.Text = string.Format(
            CultureInfo.CurrentCulture,
            _localization.GetString(prompt.CanAccept ? "ServerEditorTrustPresentedFormat" : "ServerEditorTrustReceivedFormat"),
            algorithm);
        PresentedFingerprintText.Text = prompt.Fingerprint;
        TrustedBlock.Visibility = prompt.CanAccept || prompt.TrustedFingerprint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        TrustedLabel.Text = _localization.GetString("ServerEditorTrustSavedLabel");
        TrustedFingerprintText.Text = prompt.TrustedFingerprint;

        PrimaryButtonText = prompt.AcceptKey is { } accept ? _localization.GetString(accept) : string.Empty;
        CloseButtonText = _localization.GetString(prompt.CloseKey);
        SetWorking(false);
    }

    /// <summary>The accepted key is being written and the SAME retest runs: nothing else can be pressed but Cancelar.</summary>
    public void SetWorking(bool working)
    {
        IsPrimaryButtonEnabled = !working;
        WorkingRow.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        WorkingRing.IsActive = working;
        WorkingText.Text = working ? _localization.GetString("ServerEditorTrustWorking") : string.Empty;
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

    private void FocusSafeButton()
    {
        if (FindNamed(this, "CloseButton") is Button close)
        {
            close.Focus(FocusState.Keyboard);
        }
    }

    private static FrameworkElement? FindNamed(DependencyObject root, string name)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name)
            {
                return element;
            }

            if (FindNamed(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
