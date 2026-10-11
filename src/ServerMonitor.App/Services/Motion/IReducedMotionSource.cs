namespace ServerMonitor.App.Services.Motion;

/// <summary>
/// UI.11 (Prism F09, Cortex §4). The single Reduced Motion signal every UI.11 animation goes through: Windows "Animation
/// effects" (UISettings.AnimationsEnabled) off means reduced. Custom storyboards and Composition animations are NOT
/// suppressed by the system, so every pattern asks this source and snaps to its final state when <see cref="IsReduced"/>.
/// </summary>
public interface IReducedMotionSource
{
    /// <summary>True when motion must be reduced: every pattern goes instant and lands on its final state.</summary>
    bool IsReduced { get; }

    /// <summary>Raised on the UI thread after <see cref="IsReduced"/> actually changed (never for a no-op re-read).</summary>
    event EventHandler? Changed;
}

/// <summary>The platform setting behind <see cref="SystemReducedMotionSource"/>, as a seam so the source is unit-testable.</summary>
public interface IAnimationSettings
{
    bool AnimationsEnabled { get; }

    /// <summary>May be raised on any thread (UISettings raises it on a worker thread).</summary>
    event EventHandler? AnimationsEnabledChanged;
}
