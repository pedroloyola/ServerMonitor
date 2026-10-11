using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Services;
using ServerMonitor.App.Services.Motion;
using ServerMonitor.App.ViewModels;
namespace ServerMonitor.App.Controls;
public sealed partial class OnboardingView : UserControl
{
    private OnboardingViewModel? _subscribed;
    private int _shownStep;
    private Storyboard? _dotMotion;
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
        // UI.11 F12 content-swap: a real step change (never the first presentation) fades the new step's text in; the
        // step blocks (hero, benefits, principles, methods) fade in through SaMotion.Enter as they become visible.
        var stepChanged = _shownStep != 0 && _shownStep != step;
        _shownStep = step;
        if (stepChanged)
        {
            SaMotion.PlayEnter(Heading, SaMotionEnter.Fade);
            SaMotion.PlayEnter(Subtitle, SaMotionEnter.Fade);
        }

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
        UpdateDots(dots, step, animate: stepChanged && IsLoaded && !MotionPolicy.IsReduced);
        ProgressText.Text = string.Format(Text("OnboardingProgressFormat"), step);
        AutomationProperties.SetName(ProgressText, string.Format(Text("OnboardingStepNameFormat"), step));
    }
    /// <summary>
    /// UI.11 F12 (Prism §3): the active dot GROWS 6→24 (and the previous one shrinks) over SaMotionSelectDuration,
    /// point-to-point, instead of snapping - the progress row's slide-select. Width is a layout property: this is the one
    /// documented dependent animation of UI.11 (three 6 px dots, once per step change, onboarding only); the opacity is
    /// independent. Reduced Motion / first presentation: set directly. A new step replaces a running one.
    /// </summary>
    private void UpdateDots(Border[] dots, int step, bool animate)
    {
        _dotMotion?.Stop();
        _dotMotion = null;
        if (!animate)
        {
            for (var i = 0; i < dots.Length; i++) { dots[i].Width = i == step - 1 ? 24 : 6; dots[i].Opacity = i == step - 1 ? 1 : .25; }
            return;
        }

        var resources = Application.Current.Resources;
        var duration = MotionTokens.GetTime(resources, MotionTokens.SelectDuration);
        var spline = MotionTokens.GetKeySpline(resources, MotionTokens.PointToPointKeySpline);
        var storyboard = new Storyboard();
        for (var i = 0; i < dots.Length; i++)
        {
            var active = i == step - 1;
            var current = (dots[i].ActualWidth, dots[i].Opacity);
            dots[i].Width = current.ActualWidth > 0 ? current.ActualWidth : dots[i].Width; // start from the presented width
            storyboard.Children.Add(Track(dots[i], "Width", active ? 24 : 6, duration, spline, dependent: true));
            storyboard.Children.Add(Track(dots[i], "Opacity", active ? 1 : .25, duration, spline, dependent: false));
        }

        // The final values are the resting truth once the storyboard is done (HoldEnd keeps them; Stop restores locals).
        storyboard.Completed += (_, _) =>
        {
            for (var i = 0; i < dots.Length; i++) { dots[i].Width = i == step - 1 ? 24 : 6; dots[i].Opacity = i == step - 1 ? 1 : .25; }
        };
        _dotMotion = storyboard;
        storyboard.Begin();
    }

    private static DoubleAnimationUsingKeyFrames Track(DependencyObject target, string property, double to, TimeSpan duration, MotionKeySpline spline, bool dependent)
    {
        var animation = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = dependent };
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(duration),
            Value = to,
            KeySpline = new KeySpline
            {
                ControlPoint1 = new Windows.Foundation.Point(spline.X1, spline.Y1),
                ControlPoint2 = new Windows.Foundation.Point(spline.X2, spline.Y2)
            }
        });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
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
    public void FocusHeading() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { HeadingHost.FocusHeading(); Scroller.ChangeView(null,0,null,true); });
    private void OnPanelKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Tab && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), HeadingHost))
        {
            DismissButton.Focus(FocusState.Keyboard);
            args.Handled = true;
        }
    }
    private void OnDismiss(object sender,RoutedEventArgs args) => (DataContext as OnboardingViewModel)?.Dismiss();
    private void OnBack(object sender,RoutedEventArgs args) => (DataContext as OnboardingViewModel)?.Back();
    private void OnNext(object sender,RoutedEventArgs args) { if (DataContext is not OnboardingViewModel vm) return; if (vm.Step == 3) vm.Dismiss(); else vm.Next(); }
}
