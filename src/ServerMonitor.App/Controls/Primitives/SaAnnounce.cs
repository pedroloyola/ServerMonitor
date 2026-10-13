using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.12 LIVE-01 (Prism UI12A): <c>AutomationProperties.LiveSetting</c> only declares the politeness of a live region;
/// the change itself reaches a screen reader through the <c>LiveRegionChanged</c> UIA event ("raised as notification when
/// a live region refreshes its content without having focus", Microsoft Learn, AutomationEvents). <see cref="Raise"/> is
/// the one place that raises it, with a peer created on demand: <c>FromElement</c> alone returns null for an element
/// that was Collapsed until now, so the event silently never left (SaFormField, SaInlineNotice before UI.12).
/// <para>
/// <see cref="ChangesProperty"/> (attached, XAML) announces a state TextBlock whenever its text changes (any other element:
/// its accessible name) or it becomes visible, so a declarative live region needs no code-behind.
/// </para>
/// </summary>
public static class SaAnnounce
{
    public static readonly DependencyProperty ChangesProperty = DependencyProperty.RegisterAttached(
        "Changes", typeof(bool), typeof(SaAnnounce), new PropertyMetadata(false, OnChangesChanged));

    private static readonly DependencyProperty TokensProperty = DependencyProperty.RegisterAttached(
        "Tokens", typeof(long[]), typeof(SaAnnounce), new PropertyMetadata(null));

    // UI.12 R1-01 (Prism r1): one queued announcement per element at a time (a toast shown with a new message changes
    // its name AND its visibility in the same turn - two queued raises meant two LiveRegionChanged for one content).
    private static readonly DependencyProperty PendingProperty = DependencyProperty.RegisterAttached(
        "Pending", typeof(bool), typeof(SaAnnounce), new PropertyMetadata(false));

    public static bool GetChanges(FrameworkElement element) => (bool)element.GetValue(ChangesProperty);

    public static void SetChanges(FrameworkElement element, bool value) => element.SetValue(ChangesProperty, value);

    /// <summary>Raises LiveRegionChanged on the element's peer, creating the peer if the element never had one.</summary>
    public static void Raise(UIElement element) =>
        (FrameworkElementAutomationPeer.FromElement(element) ?? FrameworkElementAutomationPeer.CreatePeerForElement(element))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

    /// <summary>
    /// Raises after the pending layout pass (an element that just became Visible has no UIA bounds before it), and only
    /// if it is still shown and on the live tree then. <paramref name="announced"/> runs when the event actually left
    /// (UI.12 B12B-02: a toast shown before its page was live is announced again on Loaded, never twice).
    /// </summary>
    public static void RaiseAfterLayout(FrameworkElement element, Action? announced = null) =>
        Coalesce(
            () => (bool)element.GetValue(PendingProperty),
            pending => element.SetValue(PendingProperty, pending),
            run => element.DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () => run()) ?? false,
            () =>
            {
                if (ShouldAnnounce(element.Visibility == Visibility.Visible, SaLiveTree.IsLive(element), ContentOf(element)))
                {
                    Raise(element);
                    announced?.Invoke();
                }
            });

    /// <summary>
    /// Pure scheduling rule (unit-tested, R1-01): while a raise is queued for the element, further requests in the same
    /// turn join it; the flag clears just before the queued raise runs, so a later change is announced again.
    /// Returns whether a new raise was queued.
    /// </summary>
    internal static bool Coalesce(Func<bool> isPending, Action<bool> setPending, Func<Action, bool> enqueue, Action raise)
    {
        if (isPending())
        {
            return false;
        }

        setPending(true);
        if (enqueue(() =>
            {
                setPending(false);
                raise();
            }))
        {
            return true;
        }

        setPending(false);
        return false;
    }

    /// <summary>Pure rule (unit-tested): only a shown, connected region with something to say is announced.</summary>
    internal static bool ShouldAnnounce(bool isVisible, bool isLive, string? content) =>
        isVisible && isLive && !string.IsNullOrWhiteSpace(content);

    // What a screen reader would speak: the text of a TextBlock, the accessible name of anything else (toast, notice).
    private static string? ContentOf(FrameworkElement element) =>
        element is TextBlock text ? text.Text : Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(element);

    private static void OnChangesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if (element.GetValue(TokensProperty) is long[] tokens)
        {
            element.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, tokens[0]);
            element.UnregisterPropertyChangedCallback(SpokenProperty(element), tokens[1]);

            element.ClearValue(TokensProperty);
        }

        if (e.NewValue is true)
        {
            var visibility = element.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, OnAnnouncedPropertyChanged);
            var spoken = element.RegisterPropertyChangedCallback(SpokenProperty(element), OnAnnouncedPropertyChanged);
            element.SetValue(TokensProperty, new[] { visibility, spoken });
        }
    }

    // What changes when the region has something new to say: a TextBlock's text, anything else's accessible name
    // (a list host whose name is its count).
    private static DependencyProperty SpokenProperty(FrameworkElement element) =>
        element is TextBlock ? TextBlock.TextProperty : Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty;

    private static void OnAnnouncedPropertyChanged(DependencyObject sender, DependencyProperty property) =>
        RaiseAfterLayout((FrameworkElement)sender);
}
