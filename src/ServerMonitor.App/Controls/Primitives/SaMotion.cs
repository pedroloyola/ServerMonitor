using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using ServerMonitor.App.Services.Motion;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>How an element enters when its Visibility becomes Visible (Prism UI11A §4.3).</summary>
public enum SaMotionEnter
{
    None,

    /// <summary>content-enter / content-swap: opacity 0→1 (SaMotionEnterDuration, linear).</summary>
    Fade,

    /// <summary>transient / reveal: opacity 0→1 plus a rise of 8 DIP (SaMotionEnterOffsetDuration, decelerate).</summary>
    Rise,

    /// <summary>modal layer: the SaDialogStyle pattern - opacity 0→1 (167, linear) and scale 1.05→1 (250, decelerate).</summary>
    Dialog
}

/// <summary>
/// UI.11 motion patterns as attached behaviours, so templates and pages opt in declaratively and every pattern goes
/// through the ONE Reduced Motion gate (<see cref="MotionPolicy"/>):
/// <list type="bullet">
/// <item><see cref="HoverFadeProperty"/> (F05): a hover/pressed overlay's Background cross-fades over SaMotionHoverDuration
/// (an implicit, Composition-backed <see cref="BrushTransition"/>; no layout). Removed while motion is reduced. Never on a
/// selection Shell (that would be the fade-out/fade-in H01 forbids) and never on an element with a RenderTransform.</item>
/// <item><see cref="EnterProperty"/> (F08/F10/F11/F12/F13/F14): plays once when the element's Visibility CHANGES to
/// Visible while it is loaded - never on first presentation, never on a theme remount (remount does not change
/// Visibility), never on a data refresh (a refresh does not toggle Visibility). Off the UI thread (Composition opacity /
/// translation / scale on the element's own visual), finite, nothing at idle. A new show replaces a running one.</item>
/// <item><see cref="ExitFadeProperty"/> (F10): the transient's exit - a SaMotionExitDuration opacity fade the platform
/// plays when the element collapses (an implicit hide animation: the logical Visibility changes at once).</item>
/// </list>
/// The logical state never waits for any of this. With motion reduced every pattern is instant.
/// </summary>
public static class SaMotion
{
    public static readonly DependencyProperty HoverFadeProperty = DependencyProperty.RegisterAttached(
        "HoverFade", typeof(bool), typeof(SaMotion), new PropertyMetadata(false, OnHoverFadeChanged));

    public static readonly DependencyProperty EnterProperty = DependencyProperty.RegisterAttached(
        "Enter", typeof(SaMotionEnter), typeof(SaMotion), new PropertyMetadata(SaMotionEnter.None, OnEnterChanged));

    public static readonly DependencyProperty ExitFadeProperty = DependencyProperty.RegisterAttached(
        "ExitFade", typeof(bool), typeof(SaMotion), new PropertyMetadata(false, OnExitFadeChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(object), typeof(SaMotion), new PropertyMetadata(null));

    public static bool GetHoverFade(FrameworkElement element) => (bool)element.GetValue(HoverFadeProperty);

    public static void SetHoverFade(FrameworkElement element, bool value) => element.SetValue(HoverFadeProperty, value);

    public static SaMotionEnter GetEnter(FrameworkElement element) => (SaMotionEnter)element.GetValue(EnterProperty);

    public static void SetEnter(FrameworkElement element, SaMotionEnter value) => element.SetValue(EnterProperty, value);

    public static bool GetExitFade(FrameworkElement element) => (bool)element.GetValue(ExitFadeProperty);

    public static void SetExitFade(FrameworkElement element, bool value) => element.SetValue(ExitFadeProperty, value);

    /// <summary>
    /// Plays an enter pattern on demand (code-driven reveals whose trigger is not the element's own Visibility, e.g. the
    /// editor's modal surface). Same gate: nothing while unloaded or with motion reduced.
    /// </summary>
    public static void PlayEnter(FrameworkElement element, SaMotionEnter enter)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (MotionRules.PlaysEnter(enter != SaMotionEnter.None, becameVisible: true, element.IsLoaded, MotionPolicy.IsReduced))
        {
            StateOf(element).Play(enter);
        }
    }

    /// <summary>Lands a running enter on its final state at once (a reveal cut short, e.g. leaving Compact mid-fade).</summary>
    public static void ResetEnter(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element.GetValue(StateProperty) is MotionState state)
        {
            state.Reset();
        }
    }

    private static void OnHoverFadeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element)
        {
            StateOf(element).Refresh();
        }
    }

    private static void OnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element)
        {
            StateOf(element).Refresh();
        }
    }

    private static void OnExitFadeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element)
        {
            StateOf(element).Refresh();
        }
    }

    private static MotionState StateOf(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is not MotionState state)
        {
            state = new MotionState(element);
            element.SetValue(StateProperty, state);
        }

        return state;
    }

    /// <summary>Per-element wiring; lives as long as the element. Subscribes to the static source only while loaded.</summary>
    private sealed class MotionState
    {
        private readonly FrameworkElement _element;
        private long _visibilityToken = -1;
        private bool _subscribed;
        private bool _hasVisual;
        private bool _translationEnabled;
        private bool _scaled;

        public MotionState(FrameworkElement element)
        {
            _element = element;
            _element.Loaded += OnLoaded;
            _element.Unloaded += OnUnloaded;
        }

        public void Refresh()
        {
            ApplyHoverTransition();
            ApplyHideAnimation();
            if (GetEnter(_element) != SaMotionEnter.None && _visibilityToken < 0)
            {
                _visibilityToken = _element.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, OnVisibilityChanged);
            }

            if (_element.IsLoaded)
            {
                Subscribe();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Subscribe();
            ApplyHoverTransition();
            ApplyHideAnimation();
            if ((GetExitFade(_element) || GetEnter(_element) != SaMotionEnter.None) && _element.Visibility == Visibility.Visible)
            {
                // A remount (out of the tree and straight back) may have played the hide animation on a visible element:
                // the element is visible, so its visual is fully shown.
                ResetVisual();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_subscribed)
            {
                MotionPolicy.Source.Changed -= OnReducedMotionChanged;
                _subscribed = false;
            }

            // A remount may raise the new Loaded before this stale Unloaded: resubscribe if the element is loaded again.
            _element.DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_element.IsLoaded)
                {
                    Subscribe();
                }
            });
        }

        private void Subscribe()
        {
            if (!_subscribed)
            {
                MotionPolicy.Source.Changed += OnReducedMotionChanged;
                _subscribed = true;
            }
        }

        private void OnReducedMotionChanged(object? sender, EventArgs e)
        {
            ApplyHoverTransition();
            ApplyHideAnimation();
            if (MotionPolicy.IsReduced)
            {
                ResetVisual(); // a running enter lands on its final state at once
            }
        }

        private void ApplyHoverTransition()
        {
            if (!GetHoverFade(_element))
            {
                return;
            }

            var transition = MotionPolicy.IsReduced
                ? null
                : new BrushTransition { Duration = MotionTokens.GetTime(Application.Current.Resources, MotionTokens.HoverDuration) };
            switch (_element)
            {
                case Border border:
                    border.BackgroundTransition = transition;
                    break;
                case Panel panel:
                    panel.BackgroundTransition = transition;
                    break;
                case ContentPresenter presenter:
                    presenter.BackgroundTransition = transition;
                    break;
            }
        }

        private void ApplyHideAnimation()
        {
            if (!GetExitFade(_element))
            {
                return;
            }

            if (MotionPolicy.IsReduced)
            {
                ElementCompositionPreview.SetImplicitHideAnimation(_element, null);
                return;
            }

            var compositor = ElementCompositionPreview.GetElementVisual(_element).Compositor;
            _hasVisual = true;
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Target = "Opacity";
            fade.InsertKeyFrame(1f, 0f, compositor.CreateLinearEasingFunction());
            fade.Duration = MotionTokens.GetTime(Application.Current.Resources, MotionTokens.ExitDuration);
            ElementCompositionPreview.SetImplicitHideAnimation(_element, fade);
        }

        private void OnVisibilityChanged(DependencyObject sender, DependencyProperty property)
        {
            var enter = GetEnter(_element);
            if (!MotionRules.PlaysEnter(enter != SaMotionEnter.None, _element.Visibility == Visibility.Visible, _element.IsLoaded, MotionPolicy.IsReduced))
            {
                if (_element.Visibility == Visibility.Visible && enter != SaMotionEnter.None)
                {
                    ResetVisual();
                }

                return;
            }

            Play(enter);
        }

        public void Play(SaMotionEnter enter)
        {
            var visual = ElementCompositionPreview.GetElementVisual(_element);
            _hasVisual = true;
            var compositor = visual.Compositor;
            var resources = Application.Current.Resources;
            var enterDuration = MotionTokens.GetTime(resources, MotionTokens.EnterDuration);
            var offsetDuration = MotionTokens.GetTime(resources, MotionTokens.EnterOffsetDuration);
            var direct = MotionTokens.GetKeySpline(resources, MotionTokens.DirectKeySpline);
            var decelerate = compositor.CreateCubicBezierEasingFunction(
                new Vector2((float)direct.X1, (float)direct.Y1), new Vector2((float)direct.X2, (float)direct.Y2));

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, compositor.CreateLinearEasingFunction());
            fade.Duration = enterDuration;
            visual.StartAnimation("Opacity", fade);

            if (enter == SaMotionEnter.Rise)
            {
                ElementCompositionPreview.SetIsTranslationEnabled(_element, true);
                _translationEnabled = true;
                var rise = compositor.CreateVector3KeyFrameAnimation();
                rise.InsertKeyFrame(0f, new Vector3(0, MotionTokens.EnterOffset, 0));
                rise.InsertKeyFrame(1f, Vector3.Zero, decelerate);
                rise.Duration = offsetDuration;
                visual.StartAnimation("Translation", rise);
            }
            else if (enter == SaMotionEnter.Dialog)
            {
                _scaled = true;
                // The centre needs the laid-out size: the scale starts right after this layout pass, while the opacity
                // (already running from 0) hides the single frame before it.
                visual.Scale = new Vector3(1.05f, 1.05f, 1f);
                _element.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    visual.CenterPoint = new Vector3((float)_element.ActualWidth / 2, (float)_element.ActualHeight / 2, 0);
                    var scale = compositor.CreateVector3KeyFrameAnimation();
                    scale.InsertKeyFrame(0f, new Vector3(1.05f, 1.05f, 1f));
                    scale.InsertKeyFrame(1f, Vector3.One, decelerate);
                    scale.Duration = offsetDuration;
                    visual.StartAnimation("Scale", scale);
                });
            }
        }

        public void Reset() => ResetVisual();

        private void ResetVisual()
        {
            if (!_hasVisual)
            {
                return; // nothing of ours ever touched this element's visual
            }

            var visual = ElementCompositionPreview.GetElementVisual(_element);
            visual.StopAnimation("Opacity");
            visual.Opacity = 1f;
            if (_translationEnabled)
            {
                visual.StopAnimation("Translation");
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
            }

            if (_scaled)
            {
                visual.StopAnimation("Scale");
                visual.Scale = Vector3.One;
            }
        }
    }
}
