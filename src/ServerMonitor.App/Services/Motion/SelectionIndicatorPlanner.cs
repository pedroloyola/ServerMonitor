namespace ServerMonitor.App.Services.Motion;

/// <summary>An item's bounds in the indicator host's coordinate space (DIP). No WinUI types, so the planner is unit-testable.</summary>
public readonly record struct IndicatorRect(double X, double Y, double Width, double Height)
{
    private const double Tolerance = 0.01;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Equal within a hundredth of a DIP (layout rounding noise is not a new target).</summary>
    public bool SameAs(IndicatorRect other) =>
        Math.Abs(X - other.X) < Tolerance && Math.Abs(Y - other.Y) < Tolerance
        && Math.Abs(Width - other.Width) < Tolerance && Math.Abs(Height - other.Height) < Tolerance;
}

/// <summary>Why the indicator is being re-planned. Only a <see cref="Selection"/> may animate.</summary>
public enum IndicatorCause
{
    FirstLayout,
    Remount,
    Resize,
    DpiChange,
    LanguageChange,
    ReducedMotionChanged,
    Selection
}

public enum IndicatorAction
{
    NoOp,
    Snap,
    Animate,
    Hide
}

public readonly record struct IndicatorPlan(IndicatorAction Action, IndicatorRect Target);

/// <summary>
/// UI.11 H01/H03 (Prism §2.3/§2.5, Cortex §2). The pure decision behind the sliding selection indicator: given the
/// items' measured bounds, the checked index and why it is asked, it says whether the ONE indicator of the track animates
/// to the target, snaps there, hides or does nothing. The logical selection (RadioButton.IsChecked / the view model) never
/// waits for it; the indicator is presentation only.
/// <list type="bullet">
/// <item>Requests are coalesced until the next flush (after layout): a selection wins over a layout change in the same
/// pass, so an auto-width item that re-measures because of its own selection still slides to its FINAL width.</item>
/// <item>Retarget, never queue: every flush plans against the newest target only; the presenter starts the animation from
/// the presented value, so rapid input converges on the last selection with exactly one animation per input.</item>
/// <item>Only a user/data selection animates. The first layout, a remount (theme refresh), a resize, a DPI or language
/// change snap - or do nothing when the target did not move (a remount mid-slide keeps the running animation).</item>
/// <item>Reduced Motion: every plan that would animate snaps, and the setting changing snaps the running animation.</item>
/// </list>
/// </summary>
public sealed class SelectionIndicatorPlanner
{
    private IndicatorCause? _pending;
    private IndicatorRect? _presented;

    /// <summary>True when a request is waiting for the next flush.</summary>
    public bool HasPending => _pending is not null;

    /// <summary>The target the indicator was last snapped or animated to; null while hidden or never presented.</summary>
    public IndicatorRect? PresentedTarget => _presented;

    /// <summary>Records a request; several before one flush coalesce into the strongest cause (Selection wins).</summary>
    public void Request(IndicatorCause cause)
    {
        if (_pending is not { } pending || cause > pending)
        {
            _pending = cause;
        }
    }

    /// <summary>The checked item's bounds, or null when nothing (visible) is checked.</summary>
    public static IndicatorRect? TargetFor(IReadOnlyList<IndicatorRect> items, int selectedIndex)
    {
        ArgumentNullException.ThrowIfNull(items);
        return selectedIndex >= 0 && selectedIndex < items.Count && !items[selectedIndex].IsEmpty ? items[selectedIndex] : null;
    }

    /// <summary>Consumes the pending request against the current layout. Without a pending request it is a NoOp.</summary>
    public IndicatorPlan Flush(IReadOnlyList<IndicatorRect> items, int selectedIndex, bool reducedMotion)
    {
        if (_pending is not { } cause)
        {
            return new IndicatorPlan(IndicatorAction.NoOp, _presented ?? default);
        }

        _pending = null;
        if (TargetFor(items, selectedIndex) is not { } target)
        {
            if (_presented is null)
            {
                return new IndicatorPlan(IndicatorAction.NoOp, default);
            }

            _presented = null;
            return new IndicatorPlan(IndicatorAction.Hide, default);
        }

        var action = Decide(cause, target, reducedMotion);
        if (action is IndicatorAction.Snap or IndicatorAction.Animate)
        {
            _presented = target;
        }

        return new IndicatorPlan(action, target);
    }

    private IndicatorAction Decide(IndicatorCause cause, IndicatorRect target, bool reducedMotion)
    {
        if (_presented is not { } presented)
        {
            return IndicatorAction.Snap; // first presentation (or after a hide): never slide in from nowhere
        }

        if (cause == IndicatorCause.ReducedMotionChanged)
        {
            return IndicatorAction.Snap; // stop whatever runs and land on the target now
        }

        if (presented.SameAs(target))
        {
            return IndicatorAction.NoOp;
        }

        return cause == IndicatorCause.Selection && !reducedMotion ? IndicatorAction.Animate : IndicatorAction.Snap;
    }
}

/// <summary>
/// UI.11 H01 (Prism §2.3.2). The rect family's 1 px top glass highlight moves with the indicator as a stroke whose alpha
/// falls off down the corner arc like the XAML <c>BorderThickness="0,1,0,0"</c> crescent it replaces (the crescent thins
/// as (1 - y/r)² along the arc and is 0 on the straight sides), so the resting look matches UI.10.
/// </summary>
public static class IndicatorHighlight
{
    /// <summary>(offset along 0..corner radius from the top edge, alpha factor) gradient stops.</summary>
    public static IReadOnlyList<(float Offset, float Alpha)> Falloff { get; } =
        [(0f, 1f), (0.25f, 0.5625f), (0.5f, 0.25f), (0.75f, 0.0625f), (1f, 0f)];
}

/// <summary>UI.11 pure indicator paint rules (unit-tested).</summary>
public static class SelectionIndicatorRules
{
    /// <summary>F20 / DD-UI11-1: the hairline is drawn only for a host that asks for it, in Light, outside High Contrast.</summary>
    public static bool DrawsLightOutline(bool hostHasOutline, bool isLightTheme, bool isHighContrast) =>
        hostHasOutline && isLightTheme && !isHighContrast;
}
