using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Controls;

public sealed partial class EmptyStateControl : UserControl
{
    public EmptyStateControl()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyStateControl), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(EmptyStateControl), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText), typeof(string), typeof(EmptyStateControl), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(EmptyStateControl), new PropertyMetadata(null));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public static readonly DependencyProperty SecondaryActionTextProperty = DependencyProperty.Register(
        nameof(SecondaryActionText), typeof(string), typeof(EmptyStateControl), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SecondaryActionCommandProperty = DependencyProperty.Register(
        nameof(SecondaryActionCommand), typeof(ICommand), typeof(EmptyStateControl), new PropertyMetadata(null));

    public string SecondaryActionText
    {
        get => (string)GetValue(SecondaryActionTextProperty);
        set => SetValue(SecondaryActionTextProperty, value);
    }

    public ICommand? SecondaryActionCommand
    {
        get => (ICommand?)GetValue(SecondaryActionCommandProperty);
        set => SetValue(SecondaryActionCommandProperty, value);
    }

    public Visibility SecondaryActionVisibility(string? text) =>
        string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
}
