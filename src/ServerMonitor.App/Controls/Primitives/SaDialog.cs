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
        "Kind", typeof(SaDialogKind), typeof(SaDialog), new PropertyMetadata(SaDialogKind.Unspecified, OnKindChanged));

    /// <summary>
    /// UI.12 LAYOUT-01 (Prism UI12A): set by SaDialogStyle. While open, the surface is clamped to the window (MinWidth 620
    /// and MaxWidth 720 never exceed the window minus a 16 margin each side; MaxHeight = window height minus margins, the
    /// content scrolls) and the commands stack, full width, when they no longer fit side by side - the Standard window's
    /// minimum is 560x640 physical px (~373 DIP at 150 %), narrower than the 620 the template asks for.
    /// </summary>
    public static readonly DependencyProperty FitsWindowProperty = DependencyProperty.RegisterAttached(
        "FitsWindow", typeof(bool), typeof(SaDialog), new PropertyMetadata(false, OnFitsWindowChanged));

    internal const double WindowMargin = 16;
    internal const double SurfaceMinWidth = 620;
    internal const double SurfaceMaxWidth = 720;
    internal const double CommandMinWidth = 196; // SaDialogCommandButtonStyle MinWidth
    internal const double CommandSpacing = 12;

    public static SaDialogKind GetKind(ContentDialog dialog) => (SaDialogKind)dialog.GetValue(KindProperty);

    public static bool GetFitsWindow(ContentDialog dialog) => (bool)dialog.GetValue(FitsWindowProperty);

    public static void SetFitsWindow(ContentDialog dialog, bool value) => dialog.SetValue(FitsWindowProperty, value);

    /// <summary>Pure rule (unit-tested): the surface bounds for a window of the given size, and whether the visible
    /// commands must stack (they need <c>n x 196 + (n-1) x 12</c> of content width; the narrowest surface decides).</summary>
    internal static DialogFit FitToWindow(double rootWidth, double rootHeight, Thickness padding, Thickness border, int commandCount)
    {
        var available = Math.Max(0, rootWidth - (2 * WindowMargin));
        var minWidth = Math.Min(SurfaceMinWidth, available);
        var maxWidth = Math.Max(minWidth, Math.Min(SurfaceMaxWidth, available));
        var maxHeight = Math.Max(0, rootHeight - (2 * WindowMargin));
        var content = minWidth - padding.Left - padding.Right - border.Left - border.Right;
        var needed = (commandCount * CommandMinWidth) + (Math.Max(0, commandCount - 1) * CommandSpacing);
        return new DialogFit(minWidth, maxWidth, maxHeight, commandCount > 1 && content < needed);
    }

    public static void SetKind(ContentDialog dialog, SaDialogKind value) => dialog.SetValue(KindProperty, value);

    /// <summary>Pure mapping (unit-tested): which button Enter invokes and which template part takes the first focus.
    /// Unspecified behaves as Confirm (the same DefaultButton the style sets).</summary>
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

    private static void OnFitsWindowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentDialog dialog)
        {
            return;
        }

        dialog.Opened -= OnFitOpened;
        if (e.NewValue is true)
        {
            dialog.Opened += OnFitOpened;
        }
    }

    private static void OnFitOpened(ContentDialog dialog, ContentDialogOpenedEventArgs args)
    {
        if (dialog.XamlRoot is not { } root)
        {
            return;
        }

        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs changed) => ApplyFit(dialog, root);
        void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs closed)
        {
            root.Changed -= OnRootChanged;
            dialog.Closed -= OnClosed;
        }

        root.Changed += OnRootChanged;
        dialog.Closed += OnClosed;
        ApplyFit(dialog, root);
    }

    private static void ApplyFit(ContentDialog dialog, XamlRoot root)
    {
        if (FindNamed(dialog, "BackgroundElement") is not Border surface)
        {
            return;
        }

        var buttons = new[] { "PrimaryButton", "SecondaryButton", "CloseButton" }
            .Select(name => FindNamed(dialog, name))
            .OfType<Button>()
            .ToList();
        var visible = buttons.Count(b => b.Visibility == Visibility.Visible && b.Content is string { Length: > 0 });
        var fit = FitToWindow(root.Size.Width, root.Size.Height, dialog.Padding, surface.BorderThickness, visible);
        surface.MinWidth = fit.MinWidth;
        surface.MaxWidth = fit.MaxWidth;
        surface.MaxHeight = fit.MaxHeight;
        if (FindNamed(dialog, "CommandStack") is StackPanel commands)
        {
            commands.Orientation = fit.StackCommands ? Orientation.Vertical : Orientation.Horizontal;
            commands.HorizontalAlignment = fit.StackCommands ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
            foreach (var button in buttons)
            {
                if (fit.StackCommands)
                {
                    button.HorizontalAlignment = HorizontalAlignment.Stretch;
                }
                else
                {
                    button.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
                }
            }
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

/// <summary>UI.12 LAYOUT-01: the clamped dialog surface bounds and the command layout for one window size.</summary>
internal readonly record struct DialogFit(double MinWidth, double MaxWidth, double MaxHeight, bool StackCommands);
