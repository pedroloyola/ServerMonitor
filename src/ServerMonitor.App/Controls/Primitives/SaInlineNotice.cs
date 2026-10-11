using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.2 inline notice. Error (Figma callout 112:18273): Manual inset surface, "!" marker in danger text, title 14
/// Semibold, message 12 muted, optional action (Content) on the right; announced assertively. Info (112:2756): no
/// surface, Information icon 15 + 12 muted text; polite.
/// </summary>
[TemplateVisualState(Name = "Info", GroupName = "SeverityStates")]
[TemplateVisualState(Name = "Error", GroupName = "SeverityStates")]
public sealed class SaInlineNotice : ContentControl
{
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(SaNoticeSeverity), typeof(SaInlineNotice),
        new PropertyMetadata(SaNoticeSeverity.Info, (d, _) => ((SaInlineNotice)d).Update()));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SaInlineNotice), new PropertyMetadata(string.Empty, (d, _) => ((SaInlineNotice)d).Update()));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(SaInlineNotice), new PropertyMetadata(string.Empty, (d, _) => ((SaInlineNotice)d).Update()));

    public static readonly DependencyProperty AnnouncePolitelyProperty = DependencyProperty.Register(nameof(AnnouncePolitely), typeof(bool), typeof(SaInlineNotice), new PropertyMetadata(false, (d,_) => ((SaInlineNotice)d).Update()));
    public bool AnnouncePolitely { get => (bool)GetValue(AnnouncePolitelyProperty); set => SetValue(AnnouncePolitelyProperty,value); }

    public SaInlineNotice()
    {
        DefaultStyleKey = typeof(SaInlineNotice);
        IsTabStop = false;
        // UI.11 F10 transient, entrance only: rises in when it appears (never at first presentation or on refresh).
        SaMotion.SetEnter(this, SaMotionEnter.Rise);
    }

    public SaNoticeSeverity Severity
    {
        get => (SaNoticeSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Update();
    }

    /// <summary>
    /// Pure rule (unit-tested; UI.5 Beacon C1 N3): the non-empty parts joined as sentences, never a doubled stop or a
    /// trailing separator ("Não foi possível atualizar as métricas.", not "…métricas.. ").
    /// </summary>
    internal static string AccessibleName(string? title, string? message)
    {
        var parts = new[] { title, message }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()).ToList();
        return parts.Count switch
        {
            0 => string.Empty,
            1 => parts[0],
            _ => string.Join(" ", parts[0].EndsWith('.') || parts[0].EndsWith('?') || parts[0].EndsWith('!') || parts[0].EndsWith('…')
                ? parts[0]
                : parts[0] + ".", parts[1])
        };
    }

    private void Update()
    {
        if (GetTemplateChild("PART_ErrorTitle") is TextBlock title) title.Visibility = string.IsNullOrWhiteSpace(Title) ? Visibility.Collapsed : Visibility.Visible;
        var error = Severity == SaNoticeSeverity.Error;
        AutomationProperties.SetLiveSetting(this, error && !AnnouncePolitely ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        AutomationProperties.SetName(this, AccessibleName(Title, Message));
        VisualStateManager.GoToState(this, error ? "Error" : "Info", useTransitions: false);
        if (IsLoaded)
        {
            FrameworkElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
