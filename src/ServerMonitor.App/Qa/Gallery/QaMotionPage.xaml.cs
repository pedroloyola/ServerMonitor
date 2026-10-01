using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ServerMonitor.App.Services.Motion;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace ServerMonitor.App.Qa.Gallery;

/// <summary>
/// QA-ONLY (UI.2 S3, F-4). Plays every motion token twice: through the XAML Storyboards in Page.Resources and
/// through animations built in code from <see cref="MotionTokens"/>. Both honour
/// <see cref="UISettings.AnimationsEnabled"/>: with animations off nothing moves and each lane jumps to its end.
/// </summary>
public sealed partial class QaMotionPage : Page
{
    private const double Travel = 320;

    private readonly UISettings _uiSettings = new();

    public QaMotionPage()
    {
        InitializeComponent();

        var resources = Application.Current.Resources;
        TokenValuesText.Text = string.Join("   ",
            MotionTokens.TimeKeys.Select(key => $"{key}={MotionTokens.GetTime(resources, key).TotalMilliseconds.ToString(CultureInfo.InvariantCulture)}ms")
                .Concat(MotionTokens.KeySplineKeys.Select(key => $"{key}={MotionTokens.GetKeySpline(resources, key)}")));
        AnimationsText.Text = _uiSettings.AnimationsEnabled
            ? "UISettings.AnimationsEnabled = true: lanes animate."
            : "UISettings.AnimationsEnabled = false: nothing moves; lanes jump to their end state.";
    }

    private void OnPlayAll(object sender, RoutedEventArgs e)
    {
        var animate = _uiSettings.AnimationsEnabled;
        AnimationsText.Text = $"UISettings.AnimationsEnabled = {animate.ToString().ToLowerInvariant()}" + (animate ? ": playing." : ": end states applied, nothing animated.");

        foreach (var storyboard in Resources.Values.OfType<Storyboard>())
        {
            storyboard.Stop();
            if (animate)
            {
                storyboard.Begin();
            }
        }

        var resources = Application.Current.Resources;
        var code = new (Border Box, string Time, string Spline)[]
        {
            (CodeFadeBox, MotionTokens.FadeDuration, MotionTokens.LinearKeySpline),
            (CodeFastBox, MotionTokens.FastDuration, MotionTokens.DirectKeySpline),
            (CodeNormalBox, MotionTokens.NormalDuration, MotionTokens.PointToPointKeySpline),
            (CodeSlowBox, MotionTokens.SlowDuration, MotionTokens.DirectKeySpline)
        };
        foreach (var (box, time, spline) in code)
        {
            var transform = (TranslateTransform)box.RenderTransform;
            if (!animate)
            {
                transform.X = Travel;
                continue;
            }

            Play(transform, "X",
                (TimeSpan.Zero, 0, null),
                (MotionTokens.GetTime(resources, time), Travel, MotionTokens.GetKeySpline(resources, spline)));
        }

        if (animate)
        {
            Play(CodeFocusBox, "Opacity",
                (TimeSpan.Zero, 0, null),
                (MotionTokens.GetTime(resources, MotionTokens.FocusPeakTime), 1, MotionTokens.GetKeySpline(resources, MotionTokens.DirectKeySpline)),
                (MotionTokens.GetTime(resources, MotionTokens.FocusFadeEndTime), 0, MotionTokens.GetKeySpline(resources, MotionTokens.LinearKeySpline)));
        }
        else
        {
            // End state of every XAML lane without animating; the focus rings end hidden.
            foreach (var box in new[] { XamlFadeBox, XamlFastBox, XamlNormalBox, XamlSlowBox })
            {
                ((TranslateTransform)box.RenderTransform).X = Travel;
            }

            XamlFocusBox.Opacity = XamlReducedBox.Opacity = CodeFocusBox.Opacity = 0;
        }
    }

    private static void Play(DependencyObject target, string property, params (TimeSpan At, double Value, MotionKeySpline? Spline)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        foreach (var (at, value, spline) in frames)
        {
            animation.KeyFrames.Add(spline is { } s
                ? new SplineDoubleKeyFrame
                {
                    KeyTime = KeyTime.FromTimeSpan(at),
                    Value = value,
                    KeySpline = new KeySpline { ControlPoint1 = new Point(s.X1, s.Y1), ControlPoint2 = new Point(s.X2, s.Y2) }
                }
                : new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(at), Value = value });
        }

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }
}
