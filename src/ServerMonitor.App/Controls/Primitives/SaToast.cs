using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.2 toast, VISUAL ONLY (Figma 112:19613 / 112:19779, success is the only designed variant): 600x80 radius 20,
/// Manual panel, success badge 28 with a tick, title 14 Semibold, message 12 muted, close button 28. No timer, no
/// queue, no positioning - the toast host with its lifecycle is behaviour for UI.5/UI.7. Close raises
/// <see cref="CloseRequested"/>; the close button is icon-only, so <see cref="CloseButtonAutomationName"/> is required.
/// </summary>
[TemplatePart(Name = CloseButtonPartName, Type = typeof(Button))]
public sealed class SaToast : Control
{
    private const string CloseButtonPartName = "PART_CloseButton";

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SaToast), new PropertyMetadata(string.Empty, (d, _) => ((SaToast)d).UpdateName()));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(SaToast), new PropertyMetadata(string.Empty, (d, _) => ((SaToast)d).UpdateName()));

    public static readonly DependencyProperty CloseButtonAutomationNameProperty = DependencyProperty.Register(
        nameof(CloseButtonAutomationName), typeof(string), typeof(SaToast), new PropertyMetadata(string.Empty, (d, _) => ((SaToast)d).UpdateName()));

    private Button? _close;

    public SaToast()
    {
        DefaultStyleKey = typeof(SaToast);
        IsTabStop = false;
        // UI.11 F10 transient: rises in (fade 167 + 8 px, 250) when shown, fades out (83) when it closes.
        SaMotion.SetEnter(this, SaMotionEnter.Rise);
        SaMotion.SetExitFade(this, true);
    }

    public event EventHandler? CloseRequested;

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

    public string CloseButtonAutomationName
    {
        get => (string)GetValue(CloseButtonAutomationNameProperty);
        set => SetValue(CloseButtonAutomationNameProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        if (_close is not null)
        {
            _close.Click -= OnCloseClick;
        }

        base.OnApplyTemplate();
        _close = GetTemplateChild(CloseButtonPartName) as Button;
        if (_close is not null)
        {
            _close.Click += OnCloseClick;
        }

        UpdateName();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateName()
    {
        AutomationProperties.SetName(this, $"{Title}. {Message}");
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        if (_close is not null)
        {
            AutomationProperties.SetName(_close, CloseButtonAutomationName);
        }
    }
}
