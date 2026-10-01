using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.2 form field (Figma "Field / *" 112:3337, error 112:16924): label 12 Medium, gap 6, the input (Content),
/// then a helper line (11 Regular muted) or - when <see cref="ErrorText"/> is set - the error message (12 Regular,
/// danger text) announced assertively. The input's UI Automation name comes from the label
/// (<c>LabeledBy</c>) and its description from the helper and error (<c>DescribedBy</c>), so a screen reader hears
/// the label, the hint and the error without the consumer wiring anything.
/// <para>
/// It does not restyle the input: an invalid TextBox uses <c>SaTextFieldErrorStyle</c> (1.5 danger border). For a
/// <see cref="SaPasswordField"/> the inner PasswordBox is the one labelled.
/// </para>
/// </summary>
[TemplatePart(Name = HeaderPartName, Type = typeof(TextBlock))]
[TemplatePart(Name = HelperPartName, Type = typeof(TextBlock))]
[TemplatePart(Name = ErrorPartName, Type = typeof(TextBlock))]
[TemplateVisualState(Name = "Valid", GroupName = "ValidationStates")]
[TemplateVisualState(Name = "Invalid", GroupName = "ValidationStates")]
public sealed class SaFormField : ContentControl
{
    private const string HeaderPartName = "PART_Header";
    private const string HelperPartName = "PART_Helper";
    private const string ErrorPartName = "PART_Error";

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SaFormField), new PropertyMetadata(string.Empty, (d, _) => ((SaFormField)d).UpdateParts()));

    public static readonly DependencyProperty HelperTextProperty = DependencyProperty.Register(
        nameof(HelperText), typeof(string), typeof(SaFormField), new PropertyMetadata(string.Empty, (d, _) => ((SaFormField)d).UpdateParts()));

    public static readonly DependencyProperty ErrorTextProperty = DependencyProperty.Register(
        nameof(ErrorText), typeof(string), typeof(SaFormField), new PropertyMetadata(string.Empty, (d, e) => ((SaFormField)d).OnErrorTextChanged((string?)e.OldValue)));

    private TextBlock? _header;
    private TextBlock? _helper;
    private TextBlock? _error;

    public SaFormField()
    {
        DefaultStyleKey = typeof(SaFormField);
        IsTabStop = false;
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string HelperText
    {
        get => (string)GetValue(HelperTextProperty);
        set => SetValue(HelperTextProperty, value);
    }

    /// <summary>Empty = valid. Non-empty switches to the Invalid state and is announced assertively.</summary>
    public string ErrorText
    {
        get => (string)GetValue(ErrorTextProperty);
        set => SetValue(ErrorTextProperty, value);
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _header = GetTemplateChild(HeaderPartName) as TextBlock;
        _helper = GetTemplateChild(HelperPartName) as TextBlock;
        _error = GetTemplateChild(ErrorPartName) as TextBlock;
        UpdateParts();
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (oldContent is FrameworkElement old)
        {
            // R2 (Cortex R-11): the replaced input no longer points at this field's label/helper/error.
            old.Loaded -= OnContentLoaded;
            var oldInput = old is SaPasswordField password ? (UIElement?)password.PasswordBox ?? password : old;
            oldInput.ClearValue(AutomationProperties.LabeledByProperty);
            AutomationProperties.GetDescribedBy(oldInput).Clear();
        }

        if (newContent is FrameworkElement element)
        {
            // A templated input (SaPasswordField) only has its inner control after its own template is applied.
            element.Loaded += OnContentLoaded;
        }

        UpdateAutomation();
    }

    private void OnContentLoaded(object sender, RoutedEventArgs e) => UpdateAutomation();

    private void OnErrorTextChanged(string? oldValue)
    {
        UpdateParts();
        if (HasError && _error is not null && !string.Equals(oldValue, ErrorText, StringComparison.Ordinal))
        {
            FrameworkElementAutomationPeer.FromElement(_error)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private void UpdateParts()
    {
        if (_helper is not null)
        {
            _helper.Visibility = !HasError && !string.IsNullOrWhiteSpace(HelperText) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_header is not null)
        {
            _header.Visibility = string.IsNullOrWhiteSpace(Header) ? Visibility.Collapsed : Visibility.Visible;
        }

        VisualStateManager.GoToState(this, HasError ? "Invalid" : "Valid", useTransitions: false);
        UpdateAutomation();
    }

    private void UpdateAutomation()
    {
        var input = Content switch
        {
            SaPasswordField password => (UIElement?)password.PasswordBox ?? password,
            UIElement element => element,
            _ => null
        };
        if (input is null || _header is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Header))
        {
            AutomationProperties.SetLabeledBy(input, _header);
        }

        var describedBy = AutomationProperties.GetDescribedBy(input);
        foreach (var part in new DependencyObject?[] { _helper, _error })
        {
            if (part is not null && !describedBy.Contains(part))
            {
                describedBy.Add(part);
            }
        }
    }
}
