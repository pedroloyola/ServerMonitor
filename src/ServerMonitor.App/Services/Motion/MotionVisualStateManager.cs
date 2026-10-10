using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Services.Motion;

/// <summary>
/// UI.11 (Cortex §4, Prism F09). The Reduced Motion gate for template-driven transitions (toggle, dialog): set as the
/// template root's <c>VisualStateManager.CustomVisualStateManager</c>, it passes <c>useTransitions</c> through only while
/// motion is not reduced, so every VisualTransition storyboard is skipped and the control lands on the target state at
/// once. The documented VSM extension point - no template forks, the visual states themselves are untouched.
/// </summary>
public sealed partial class MotionVisualStateManager : VisualStateManager
{
    /// <summary>Pure rule (unit-tested): transitions run only when requested AND motion is not reduced.</summary>
    internal static bool UseTransitions(bool requested, bool reducedMotion) => requested && !reducedMotion;

    protected override bool GoToStateCore(
        Control control,
        FrameworkElement templateRoot,
        string stateName,
        VisualStateGroup group,
        VisualState state,
        bool useTransitions) =>
        base.GoToStateCore(control, templateRoot, stateName, group, state, UseTransitions(useTransitions, MotionPolicy.IsReduced));
}
