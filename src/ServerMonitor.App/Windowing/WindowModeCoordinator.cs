using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Windowing;

/// <summary>
/// The Standard ⇄ Compact state machine. It sequences each transition deterministically —
/// capture the outgoing mode's bounds, configure the presenter for the incoming mode, resolve and
/// apply recovered bounds, set always-on-top, persist, then announce the new mode — so the window
/// is never left half-transitioned (topmost while Standard, or resized before its content swaps).
/// The heavy lifting is delegated to the fakeable <see cref="IWindowPlacementAdapter"/> and the
/// pure <see cref="WindowPlacementResolver"/>, which is what makes the whole class unit-testable.
/// </summary>
public sealed class WindowModeCoordinator : IWindowModeCoordinator
{
    private readonly IWindowPlacementAdapter _adapter;
    private readonly IWindowPlacementStore _store;
    private readonly ILogger<WindowModeCoordinator> _logger;

    private WindowPlacementSettings _settings = WindowPlacementSettings.Default;
    private WindowMode _mode = WindowMode.Standard;
    private bool _initialized;
    private bool _applyingBounds;

    public WindowModeCoordinator(
        IWindowPlacementAdapter adapter,
        IWindowPlacementStore store,
        ILogger<WindowModeCoordinator> logger)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event EventHandler<WindowMode>? ModeChanged;

    public WindowMode CurrentMode => _mode;

    public bool CompactAlwaysOnTop => _settings.CompactAlwaysOnTop;

    public bool IsApplyingBounds => _applyingBounds;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _settings = _store.Load();
        _mode = _settings.Mode;
        _logger.LogInformation("Restoring window in {Mode} mode.", _mode);
        ApplyMode(_mode);
    }

    public void SwitchTo(WindowMode mode)
    {
        if (!_initialized)
        {
            _logger.LogDebug("Ignoring mode switch before initialization.");
            return;
        }

        if (mode == _mode)
        {
            return;
        }

        // Remember where the mode we are leaving currently sits, so returning to it restores it.
        CaptureBoundsInto(_mode);
        _mode = mode;
        _settings = _settings with { Mode = mode };
        ApplyMode(mode);
        _store.Save(_settings);
        _logger.LogInformation("Switched window to {Mode} mode.", mode);
    }

    public void Toggle() =>
        SwitchTo(_mode == WindowMode.Compact ? WindowMode.Standard : WindowMode.Compact);

    public void SetCompactAlwaysOnTop(bool enabled)
    {
        if (_settings.CompactAlwaysOnTop == enabled)
        {
            return;
        }

        _settings = _settings with { CompactAlwaysOnTop = enabled };

        // Always-on-top is a compact-only property; in Standard we only remember the preference.
        if (_mode == WindowMode.Compact)
        {
            _adapter.SetAlwaysOnTop(enabled);
        }

        _store.Save(_settings);
        _logger.LogInformation("Compact always-on-top set to {Enabled}.", enabled);
    }

    public void CaptureCurrentBounds()
    {
        if (_initialized)
        {
            CaptureBoundsInto(_mode);
        }
    }

    public void PersistCurrentBounds()
    {
        if (!_initialized)
        {
            return;
        }

        // Capture is best-effort (a no-op while minimized); we still persist the last good bounds
        // already held in memory, so a window minimized to the tray and then closed reopens where
        // it last sat.
        CaptureBoundsInto(_mode);
        _store.Save(_settings);
    }

    private void ApplyMode(WindowMode mode)
    {
        var constraints = WindowSizeConstraints.For(mode);
        _applyingBounds = true;
        try
        {
            _adapter.ConfigurePresenter(mode, constraints);

            var displays = _adapter.GetDisplays();
            var (savedBounds, savedDpi) = mode == WindowMode.Compact
                ? (_settings.CompactBounds, _settings.CompactDpiScalePercent)
                : (_settings.StandardBounds, _settings.StandardDpiScalePercent);

            var resolved = WindowPlacementResolver.Resolve(savedBounds, savedDpi, displays, constraints);
            _adapter.ApplyBounds(resolved);
            _adapter.SetAlwaysOnTop(mode == WindowMode.Compact && _settings.CompactAlwaysOnTop);

            // Record what was actually applied so the in-memory preference always reflects reality,
            // even before the user moves the window (important for the minimize-then-close path).
            CaptureBoundsInto(mode);
        }
        finally
        {
            _applyingBounds = false;
        }

        ModeChanged?.Invoke(this, mode);
    }

    private bool CaptureBoundsInto(WindowMode mode)
    {
        if (!_adapter.IsAttached)
        {
            return false;
        }

        // Null placement means the window is minimized/not ready; its geometry is meaningless, so
        // keep the last good bounds rather than persisting a bogus rectangle.
        if (_adapter.GetPlacement() is not { } placement || !WindowPlacementResolver.IsSane(placement.Bounds))
        {
            return false;
        }

        _settings = mode == WindowMode.Compact
            ? _settings with { CompactBounds = placement.Bounds, CompactDpiScalePercent = placement.DpiScalePercent }
            : _settings with { StandardBounds = placement.Bounds, StandardDpiScalePercent = placement.DpiScalePercent };
        return true;
    }
}
