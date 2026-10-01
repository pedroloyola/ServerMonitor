using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.2 password field (Figma "Input / Palavra-passe" 112:3693): the TextField look plus an eye toggle (Hugeicons
/// View, 17) 15 px from the right edge that reveals or hides the password.
/// <para>
/// Security invariant (UI.0 §5.11): the password is NEVER bound - there is no Password dependency property. The
/// owner reads <see cref="PasswordBox"/>.Password in code, exactly like the production dialogs do today. Revealing
/// only switches <see cref="Microsoft.UI.Xaml.Controls.PasswordBox.PasswordRevealMode"/> between Hidden and Visible.
/// </para>
/// The eye is icon-only, so <see cref="RevealButtonAutomationName"/> is required (T-17).
/// </summary>
[TemplatePart(Name = PasswordBoxPartName, Type = typeof(PasswordBox))]
[TemplatePart(Name = RevealButtonPartName, Type = typeof(ToggleButton))]
public sealed class SaPasswordField : Control
{
    private const string PasswordBoxPartName = "PART_PasswordBox";
    private const string RevealButtonPartName = "PART_RevealButton";

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(SaPasswordField), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty RevealButtonAutomationNameProperty = DependencyProperty.Register(
        nameof(RevealButtonAutomationName), typeof(string), typeof(SaPasswordField),
        new PropertyMetadata(string.Empty, (d, _) => ((SaPasswordField)d).UpdateRevealButtonName()));

    private ToggleButton? _revealButton;

    public SaPasswordField()
    {
        DefaultStyleKey = typeof(SaPasswordField);
        IsTabStop = false;
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>Accessible name of the eye toggle (localized by the consumer). Required.</summary>
    public string RevealButtonAutomationName
    {
        get => (string)GetValue(RevealButtonAutomationNameProperty);
        set => SetValue(RevealButtonAutomationNameProperty, value);
    }

    /// <summary>The inner PasswordBox, for the owner to read <c>Password</c> in code. Null until the template is applied.</summary>
    public PasswordBox? PasswordBox { get; private set; }

    public bool IsPasswordRevealed => PasswordBox?.PasswordRevealMode == PasswordRevealMode.Visible;

    protected override void OnApplyTemplate()
    {
        if (_revealButton is not null)
        {
            _revealButton.Checked -= OnRevealChanged;
            _revealButton.Unchecked -= OnRevealChanged;
        }

        base.OnApplyTemplate();
        PasswordBox = GetTemplateChild(PasswordBoxPartName) as PasswordBox;
        _revealButton = GetTemplateChild(RevealButtonPartName) as ToggleButton;
        if (_revealButton is not null)
        {
            _revealButton.Checked += OnRevealChanged;
            _revealButton.Unchecked += OnRevealChanged;
        }

        UpdateRevealButtonName();
        OnRevealChanged(this, new RoutedEventArgs());
    }

    private void OnRevealChanged(object sender, RoutedEventArgs e)
    {
        if (PasswordBox is not null)
        {
            PasswordBox.PasswordRevealMode = _revealButton?.IsChecked == true ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        }
    }

    private void UpdateRevealButtonName()
    {
        if (_revealButton is not null)
        {
            AutomationProperties.SetName(_revealButton, RevealButtonAutomationName);
        }
    }
}
