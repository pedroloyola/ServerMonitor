namespace ServerMonitor.App.Services.Motion;

/// <summary>UI.11 pure motion rules (no WinUI types), unit-tested; the attached behaviours apply them.</summary>
public static class MotionRules
{
    /// <summary>
    /// An enter pattern plays only for a real change to Visible of an element that is already in the tree, with motion
    /// on: never at first presentation (not loaded yet), never on a theme remount or a data refresh (no Visibility
    /// change), never with Reduced Motion.
    /// </summary>
    public static bool PlaysEnter(bool hasEnter, bool becameVisible, bool isLoaded, bool reducedMotion) =>
        hasEnter && becameVisible && isLoaded && !reducedMotion;
}
