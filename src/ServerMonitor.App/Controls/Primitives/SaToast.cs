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
    private readonly ToastAnnouncement _announcement = new();

    public SaToast()
    {
        DefaultStyleKey = typeof(SaToast);
        IsTabStop = false;
        // UI.11 F10 transient: rises in (fade 167 + 8 px, 250) when shown, fades out (83) when it closes.
        SaMotion.SetEnter(this, SaMotionEnter.Rise);
        SaMotion.SetExitFade(this, true);
        // UI.12 LIVE-01: a toast is shown by Visibility; it is announced then (it closes on a timer, never focused).
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            _announcement.VisibilityChanged(Visibility == Visibility.Visible);
            SaAnnounce.RaiseAfterLayout(this, _announcement.Announced);
        });
        // UI.12 B12B-02: a toast made Visible while its page was not live yet (shown during the navigation, x:Bind before
        // Loaded) had its raise dropped - it is announced once the page is live, unless it already was.
        Loaded += (_, _) =>
        {
            if (_announcement.ShouldAnnounceOnLoaded(Visibility == Visibility.Visible))
            {
                SaAnnounce.RaiseAfterLayout(this, _announcement.Announced);
            }
        };
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

    // UI.12 LIVE-01: without a peer the toast (its text is Raw) was not in the UIA tree, so its LiveSetting meant nothing.
    protected override AutomationPeer OnCreateAutomationPeer() => new SaToastAutomationPeer(this);

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateName()
    {
        var name = SaInlineNotice.AccessibleName(Title, Message);
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        if (_close is not null)
        {
            AutomationProperties.SetName(_close, CloseButtonAutomationName);
        }

        _announcement.ContentChanged(name);
        if (IsLoaded)
        {
            SaAnnounce.RaiseAfterLayout(this, _announcement.Announced);
        }
    }
}

/// <summary>
/// UI.12 B12B-02 (Beacon: the "Servidor ocultado" toast raised 0 LiveRegionChanged): whether the toast's current content
/// still has to be said. Shown or changed = not said yet; said once the event actually left; hidden = the next showing is
/// new. A toast already said is never repeated on a later Loaded (navigating back while it is open), nor when the same
/// content is set again (a template re-applied).
/// </summary>
internal sealed class ToastAnnouncement
{
    private bool _announced;
    private string? _content;

    public void VisibilityChanged(bool visible)
    {
        if (!visible)
        {
            _announced = false;
        }
    }

    public void ContentChanged(string? content)
    {
        if (!string.Equals(content, _content, StringComparison.Ordinal))
        {
            _content = content;
            _announced = false;
        }
    }

    public void Announced() => _announced = true;

    public bool ShouldAnnounceOnLoaded(bool visible) => visible && !_announced;
}

/// <summary>UI.12 LIVE-01: the toast is one named status group ("Título. Mensagem") holding its close button.</summary>
public sealed class SaToastAutomationPeer(SaToast owner) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    protected override string GetClassNameCore() => nameof(SaToast);
}
