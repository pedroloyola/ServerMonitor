using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.2 R1 (Cortex R-2, Beacon F-1): the dialog behaviour that goes with <c>SaDialogStyle</c>, set as an attached
/// property on the native ContentDialog. Confirm: Enter confirms (DefaultButton = Primary) and focus starts on the
/// primary button. Destructive: the primary button wears the destructive style, but Enter and the initial focus land
/// on the SAFE button (Close / "Cancelar") - a stray Enter never removes anything. The Sa template's
/// DefaultButtonStates are empty, so the default button keeps its Sa look (never the legacy AccentButtonStyle).
/// </summary>
public static class SaDialog
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(SaDialogKind), typeof(SaDialog), new PropertyMetadata(SaDialogKind.Confirm, OnKindChanged));

    public static SaDialogKind GetKind(ContentDialog dialog) => (SaDialogKind)dialog.GetValue(KindProperty);

    public static void SetKind(ContentDialog dialog, SaDialogKind value) => dialog.SetValue(KindProperty, value);

    /// <summary>Pure mapping (unit-tested): which button Enter invokes and which template part takes the first focus.</summary>
    internal static (ContentDialogButton DefaultButton, string InitialFocusPart, bool DestructivePrimary) BehaviourFor(SaDialogKind kind) =>
        kind == SaDialogKind.Destructive
            ? (ContentDialogButton.Close, "CloseButton", true)
            : (ContentDialogButton.Primary, "PrimaryButton", false);

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentDialog dialog)
        {
            return;
        }

        var behaviour = BehaviourFor((SaDialogKind)e.NewValue);
        dialog.DefaultButton = behaviour.DefaultButton;
        if (behaviour.DestructivePrimary && Application.Current.Resources.TryGetValue("SaDestructiveButtonStyle", out var style))
        {
            dialog.PrimaryButtonStyle = (Style)style;
        }

        dialog.Opened -= OnOpened;
        dialog.Opened += OnOpened;
    }

    private static void OnOpened(ContentDialog dialog, ContentDialogOpenedEventArgs args)
    {
        var part = BehaviourFor(GetKind(dialog)).InitialFocusPart;
        if (FindNamed(dialog, part) is Button button)
        {
            button.Focus(FocusState.Keyboard);
        }
    }

    private static FrameworkElement? FindNamed(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
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
