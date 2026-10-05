using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
namespace ServerMonitor.App.Controls;
public sealed partial class OnboardingView : UserControl
{
    private OnboardingViewModel? _subscribed;
    public ILocalizationService? Localization { get; set; }
    public OnboardingView()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
        DataContextChanged += (_, _) => { if (IsLoaded) Subscribe(); };
        SizeChanged += (_, _) => { Panel.Width = Math.Min(1040, ActualWidth); Reflow(); };
        KeyDown += OnPanelKeyDown;
    }
    private void Subscribe()
    {
        Unsubscribe(); _subscribed = DataContext as OnboardingViewModel;
        if (_subscribed is not null) _subscribed.PropertyChanged += OnStateChanged;
        UpdateStep();
    }
    private void Unsubscribe() { if (_subscribed is not null) _subscribed.PropertyChanged -= OnStateChanged; _subscribed = null; }
    private void OnStateChanged(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(OnboardingViewModel.Step)) { UpdateStep(); FocusHeading(); } }
    private string Text(string key) => Localization?.GetString(key) ?? string.Empty;
    private void UpdateStep()
    {
        var step = (DataContext as OnboardingViewModel)?.Step ?? 1;
        HeroBrand.Visibility = Benefits.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        HeroShield.Visibility = Principles.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Methods.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        var prefix = step == 1 ? "Welcome" : step == 2 ? "Local" : "Setup";
        Heading.Text = Text("Onboarding" + prefix + "Title.Text");
        Heading.Style = (Style)Application.Current.Resources[step == 1 ? "SaOnboardingHeroTextStyle" : "SaOnboardingTitleTextStyle"];
        Subtitle.Style = (Style)Application.Current.Resources[step == 1 ? "SaOnboardingLeadTextStyle" : "SaOnboardingBodyTextStyle"];
        Subtitle.Text = Text("Onboarding" + prefix + "Body.Text");
        StepNote.Visibility = step == 1 ? Visibility.Collapsed : Visibility.Visible;
        StepNote.Text = Text(step == 2 ? "OnboardingReadOnly.Text" : "OnboardingMore.Text");
        BackButton.Opacity = step == 1 ? 0 : 1;
        BackButton.IsTabStop = BackButton.IsHitTestVisible = step != 1;
        AutomationProperties.SetAccessibilityView(BackButton, step == 1 ? AccessibilityView.Raw : AccessibilityView.Content);
        NextButton.Content = Text(step == 1 ? "OnboardingStart.Content" : step == 2 ? "OnboardingContinue.Content" : "OnboardingExplore.Content");
        NextButton.Style = (Style)Application.Current.Resources[step == 3 ? "SaOnboardingSecondaryButtonStyle" : "SaOnboardingPrimaryButtonStyle"];
        var dots = new[] { Dot1, Dot2, Dot3 };
        for (var i=0;i<dots.Length;i++) { dots[i].Width = i == step-1 ? 24 : 6; dots[i].Opacity = i == step-1 ? 1 : .25; }
        ProgressText.Text = string.Format(Text("OnboardingProgressFormat"), step);
        AutomationProperties.SetName(ProgressText, string.Format(Text("OnboardingStepNameFormat"), step));
    }
    private void Reflow()
    {
        var panelWidth = Math.Min(1040, ActualWidth);
        var wide = panelWidth >= 900;
        Benefits.RowSpacing = wide ? 0 : 16;
        Methods.RowSpacing = wide ? 0 : 24;
        Panel.Padding = (Thickness)Application.Current.Resources[panelWidth < 700 ? "SaPagePaddingCompact" : "SaOnboardingPadding"];
        var benefits = new[] { Benefit0, Benefit1, Benefit2 };
        for (var i=0;i<benefits.Length;i++) { Grid.SetColumn(benefits[i],wide ? i : 0); Grid.SetRow(benefits[i],wide ? 0 : i); Grid.SetColumnSpan(benefits[i],wide ? 1 : 3); }
        Grid.SetColumn(Method1,wide ? 1 : 0); Grid.SetRow(Method1,wide ? 0 : 1);
        Grid.SetColumnSpan(Method0,wide ? 1 : 2); Grid.SetColumnSpan(Method1,wide ? 1 : 2);
    }
    public void FocusHeading() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { _ = ServerMonitor.App.Views.ShellPageFocus.FocusTextAsync(Heading); Scroller.ChangeView(null,0,null,true); });
    private void OnPanelKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Tab && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), Heading))
        {
            DismissButton.Focus(FocusState.Keyboard);
            args.Handled = true;
        }
    }
    private void OnDismiss(object sender,RoutedEventArgs args) => (DataContext as OnboardingViewModel)?.Dismiss();
    private void OnBack(object sender,RoutedEventArgs args) => (DataContext as OnboardingViewModel)?.Back();
    private void OnNext(object sender,RoutedEventArgs args) { if (DataContext is not OnboardingViewModel vm) return; if (vm.Step == 3) vm.Dismiss(); else vm.Next(); }
}
