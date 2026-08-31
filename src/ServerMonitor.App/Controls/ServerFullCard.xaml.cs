using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using ServerMonitor.App.ViewModels;
using Windows.UI.ViewManagement;

namespace ServerMonitor.App.Controls;

public sealed partial class ServerFullCard : UserControl
{
    private static readonly UISettings UiSettings = new();
    private ServerCardViewModel? _viewModel;

    // A focus pulse is "armed" the moment a request is consumed, but only PLAYS once this card is actually
    // rendered (has a non-zero size and is visible). On a cold start the deep-link resolves during the
    // initial load — before the card exists on screen — so firing the storyboard immediately is invisible.
    private bool _pulseArmed;
    private bool _awaitingLayout;

    public ServerFullCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        Rebind(args.NewValue as ServerCardViewModel);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The Dashboard page and its view models are singletons, so on navigating back this card is reused
        // with the SAME DataContext — DataContextChanged does NOT re-fire, so we must re-subscribe here
        // (idempotently) or the focus pulse would be lost after the first navigation away (QA-1, Atlas M1).
        Rebind(DataContext as ServerCardViewModel);

        // A pulse that was armed while this card was unloaded (or before it had a size) can now try to play.
        TryFirePulse();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Unsubscribe();
        StopAwaitingLayout();
    }

    // Idempotent (re)binding: drop any existing subscription, then subscribe to the current view model.
    private void Rebind(ServerCardViewModel? viewModel)
    {
        Unsubscribe();
        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // A focus request may have set the flag before this card control existed or re-bound (cold
            // start: the deep-link resolves during initial load, before the card is realized).
            if (_viewModel.IsFocusHighlighted)
            {
                ArmPulse();
            }
        }
    }

    private void Unsubscribe()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerCardViewModel.IsFocusHighlighted) &&
            _viewModel?.IsFocusHighlighted == true)
        {
            ArmPulse();
        }
    }

    // Consume the VM flag immediately (so a later re-focus of the SAME card re-triggers) and schedule the
    // pulse to play once the card is actually rendered.
    private void ArmPulse()
    {
        if (_viewModel is not null)
        {
            _viewModel.IsFocusHighlighted = false;
        }

        _pulseArmed = true;
        TryFirePulse();
    }

    // Play the armed pulse only when the card has a real, visible layout; otherwise wait for the first
    // layout pass (the cold-start window paint) and retry — a storyboard on an unrendered card is invisible.
    private void TryFirePulse()
    {
        if (!_pulseArmed)
        {
            return;
        }

        if (IsLoaded && Visibility == Visibility.Visible && ActualWidth > 0 && ActualHeight > 0)
        {
            _pulseArmed = false;
            StopAwaitingLayout();
            PulseFocusRing();
        }
        else if (!_awaitingLayout)
        {
            _awaitingLayout = true;
            LayoutUpdated += OnLayoutUpdatedForPulse;
        }
    }

    private void OnLayoutUpdatedForPulse(object? sender, object e) => TryFirePulse();

    private void StopAwaitingLayout()
    {
        if (_awaitingLayout)
        {
            LayoutUpdated -= OnLayoutUpdatedForPulse;
            _awaitingLayout = false;
        }
    }

    // QA-1: briefly pulse the accent focus ring so the user can SEE which server the widget selected —
    // even when the card is already on screen (bring-into-view alone is invisible). Reduced-motion aware:
    // when the OS has animations disabled, hold a static ring for ~2s instead of fading.
    private void PulseFocusRing()
    {
        var frames = new DoubleAnimationUsingKeyFrames();
        Storyboard.SetTarget(frames, FocusRing);
        Storyboard.SetTargetProperty(frames, "Opacity");

        if (UiSettings.AnimationsEnabled)
        {
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
            frames.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(180), Value = 1 });
            frames.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(1600), Value = 0 });
        }
        else
        {
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 1 });
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(2), Value = 0 });
        }

        var storyboard = new Storyboard();
        storyboard.Children.Add(frames);
        storyboard.Begin();
    }
}
