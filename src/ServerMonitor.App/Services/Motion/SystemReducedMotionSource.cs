using Windows.UI.ViewManagement;

namespace ServerMonitor.App.Services.Motion;

/// <summary>
/// UI.11 (Cortex §4). Reads <see cref="IAnimationSettings.AnimationsEnabled"/> once at construction and again on every
/// change notification, which is marshalled to the UI dispatcher before the value is re-read and <see cref="Changed"/> is
/// raised - so consumers only ever run on the UI thread. <c>forceReduced</c> is the Debug-only <c>--qa-reduced-motion</c>
/// harness (always false in Release): it pins reduced on without touching the user's system setting.
/// Holds ONE settings object for the process lifetime (UISettings stops raising events once collected).
/// </summary>
public sealed class SystemReducedMotionSource : IReducedMotionSource, IDisposable
{
    private readonly IAnimationSettings _settings;
    private readonly Func<Action, bool> _postToUi;
    private readonly bool _forceReduced;
    private bool? _override;
    private bool _isReduced;
    private bool _disposed;

    public SystemReducedMotionSource(IAnimationSettings settings, Func<Action, bool> postToUi, bool forceReduced)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        _forceReduced = forceReduced;
        _isReduced = Compute();
        _settings.AnimationsEnabledChanged += OnSettingsChanged;
    }

    public bool IsReduced => _isReduced;

    public event EventHandler? Changed;

    /// <summary>
    /// QA-only seam (the motion gallery's "flip" button): overrides the system value until cleared with null, raising
    /// <see cref="Changed"/> like a live setting change would. Must be called on the UI thread.
    /// </summary>
    internal void SetOverride(bool? reduced)
    {
        _override = reduced;
        Refresh();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.AnimationsEnabledChanged -= OnSettingsChanged;
    }

    private bool Compute() => _forceReduced || (_override ?? !_settings.AnimationsEnabled);

    // Any thread: never touch state here, only hop to the UI thread.
    private void OnSettingsChanged(object? sender, EventArgs e) => _postToUi(Refresh);

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var reduced = Compute();
        if (reduced == _isReduced)
        {
            return;
        }

        _isReduced = reduced;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>The real <see cref="IAnimationSettings"/>: one <see cref="UISettings"/> (AnimationsEnabledChanged since 19041).</summary>
public sealed class UISettingsAnimationSettings : IAnimationSettings
{
    private readonly UISettings _settings = new();
    private EventHandler? _changed;

    public bool AnimationsEnabled => _settings.AnimationsEnabled;

    public event EventHandler? AnimationsEnabledChanged
    {
        add
        {
            if (_changed is null)
            {
                _settings.AnimationsEnabledChanged += OnChanged;
            }

            _changed += value;
        }
        remove
        {
            _changed -= value;
            if (_changed is null)
            {
                _settings.AnimationsEnabledChanged -= OnChanged;
            }
        }
    }

    private void OnChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) => _changed?.Invoke(this, EventArgs.Empty);
}
