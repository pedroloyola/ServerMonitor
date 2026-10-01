using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.2 skeleton block (Figma 112:15602: radius 6, Manual secondary text @ .14). Static by default. With
/// <see cref="IsPulsing"/> it pulses (DERIVED - Figma has no animation) ONLY when <c>UISettings.AnimationsEnabled</c>
/// is true, using the Motion tokens; otherwise it stays static. Decorative for UI Automation (the page announces
/// "loading").
/// </summary>
public sealed class SaSkeleton : Control
{
    public static readonly DependencyProperty IsPulsingProperty = DependencyProperty.Register(
        nameof(IsPulsing), typeof(bool), typeof(SaSkeleton), new PropertyMetadata(false, (d, _) => ((SaSkeleton)d).UpdatePulse()));

    private readonly UISettings _uiSettings = new();
    private Storyboard? _pulse;

    public SaSkeleton()
    {
        DefaultStyleKey = typeof(SaSkeleton);
        IsTabStop = false;
        Loaded += (_, _) => UpdatePulse();
        Unloaded += (_, _) => StopPulse();
    }

    public bool IsPulsing
    {
        get => (bool)GetValue(IsPulsingProperty);
        set => SetValue(IsPulsingProperty, value);
    }

    /// <summary>True when the pulse may run: requested AND the system allows animations.</summary>
    public static bool ShouldPulse(bool requested, bool animationsEnabled) => requested && animationsEnabled;

    private void UpdatePulse()
    {
        StopPulse();
        if (!IsLoaded || !ShouldPulse(IsPulsing, _uiSettings.AnimationsEnabled))
        {
            return;
        }

        var half = Services.Motion.MotionTokens.GetTime(Application.Current.Resources, Services.Motion.MotionTokens.FocusFadeEndTime) / 2;
        var animation = new DoubleAnimation { From = 1, To = 0.45, Duration = half, AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(animation, this);
        Storyboard.SetTargetProperty(animation, nameof(Opacity));
        _pulse = new Storyboard();
        _pulse.Children.Add(animation);
        _pulse.Begin();
    }

    private void StopPulse()
    {
        _pulse?.Stop();
        _pulse = null;
        Opacity = 1;
    }
}
