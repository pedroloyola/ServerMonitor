using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.2 two-level breadcrumb (Figma 112:1786 / 112:2023: h22, gap 8, parent 12 muted, chevron 14, current 12 text).
/// The parent is a button (raises <see cref="ParentInvoked"/>); the current level is plain text.
/// </summary>
[TemplatePart(Name = ParentPartName, Type = typeof(Button))]
public sealed class SaBreadcrumb : Control
{
    private const string ParentPartName = "PART_Parent";

    public static readonly DependencyProperty ParentTextProperty = DependencyProperty.Register(
        nameof(ParentText), typeof(string), typeof(SaBreadcrumb), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CurrentTextProperty = DependencyProperty.Register(
        nameof(CurrentText), typeof(string), typeof(SaBreadcrumb), new PropertyMetadata(string.Empty));

    private Button? _parent;

    public SaBreadcrumb()
    {
        DefaultStyleKey = typeof(SaBreadcrumb);
        IsTabStop = false;
    }

    public event EventHandler? ParentInvoked;

    public string ParentText
    {
        get => (string)GetValue(ParentTextProperty);
        set => SetValue(ParentTextProperty, value);
    }

    public string CurrentText
    {
        get => (string)GetValue(CurrentTextProperty);
        set => SetValue(CurrentTextProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        if (_parent is not null)
        {
            _parent.Click -= OnParentClick;
        }

        base.OnApplyTemplate();
        _parent = GetTemplateChild(ParentPartName) as Button;
        if (_parent is not null)
        {
            _parent.Click += OnParentClick;
        }
    }

    private void OnParentClick(object sender, RoutedEventArgs e) => ParentInvoked?.Invoke(this, EventArgs.Empty);
}
