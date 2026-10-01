using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace ServerMonitor.App.Qa.Gallery;

/// <summary>
/// QA-ONLY (UI.2 gallery screenshot contract). Puts a static sample into a visual state once it is loaded, so hover /
/// pressed / focus samples can be captured without mouse automation. Comma-separated visual-state names go to
/// <see cref="VisualStateManager.GoToState"/>; three pseudo-states act instead: <c>Focus</c> (keyboard focus, so the
/// system focus visual shows - only one element per page can hold it), <c>OpenDropDown</c> (ComboBox) and
/// <c>ShowFlyout</c> (the element's attached flyout). A real pointer over the sample naturally replaces the state.
/// </summary>
public static class QaForcedState
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(string), typeof(QaForcedState), new PropertyMetadata(null, OnStateChanged));

    public static string? GetState(DependencyObject element) => (string?)element.GetValue(StateProperty);

    public static void SetState(DependencyObject element, string? value) => element.SetValue(StateProperty, value);

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element)
        {
            element.Loaded -= OnLoaded;
            element.Loaded += OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        // After the control has applied its own initial states.
        element.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Apply(element));
    }

    private static void Apply(FrameworkElement element)
    {
        foreach (var state in (GetState(element) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (state)
            {
                case "Focus" when element is Control control:
                    control.Focus(FocusState.Keyboard);
                    break;
                case "OpenDropDown" when element is ComboBox comboBox:
                    comboBox.IsDropDownOpen = true;
                    break;
                case "ShowFlyout":
                    FlyoutBase.ShowAttachedFlyout(element);
                    break;
                default:
                    if (element is Control target)
                    {
                        VisualStateManager.GoToState(target, state, useTransitions: false);
                    }

                    break;
            }
        }
    }
}
