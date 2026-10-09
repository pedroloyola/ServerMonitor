using Microsoft.UI.Dispatching;

namespace ServerMonitor.App.Services.Motion;

/// <summary>
/// UI.11. The process-wide access point to the Reduced Motion source for the XAML-instantiated motion pieces (the
/// template <see cref="MotionVisualStateManager"/>, the attached SaSlidingSelection / SaMotion behaviours), which cannot
/// take constructor dependencies. The App constructor installs the source once, on the UI thread, before any window; a
/// launch that never installs one (the component gallery) gets the system source created lazily on first use.
/// </summary>
public static class MotionPolicy
{
    private static IReducedMotionSource? _source;

    public static IReducedMotionSource Source => _source ??= CreateSystemSource(forceReduced: false);

    /// <summary>True when motion must be reduced right now.</summary>
    public static bool IsReduced => Source.IsReduced;

    /// <summary>Installs the process source (once, on the UI thread, before any window).</summary>
    public static void Install(IReducedMotionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Cortex R-4: a replaced source releases its UISettings subscription (the gallery path installs its own).
        if (!ReferenceEquals(_source, source))
        {
            (_source as IDisposable)?.Dispose();
        }

        _source = source;
    }

    /// <summary>The real source bound to the calling (UI) thread's dispatcher.</summary>
    public static SystemReducedMotionSource CreateSystemSource(bool forceReduced)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The Reduced Motion source must be created on the UI thread.");
        return new SystemReducedMotionSource(new UISettingsAnimationSettings(), action => dispatcher.TryEnqueue(() => action()), forceReduced);
    }
}
