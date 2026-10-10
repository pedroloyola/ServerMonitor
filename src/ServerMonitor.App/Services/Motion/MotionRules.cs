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

    /// <summary>
    /// UI.11 B11-2 (Beacon UI.11D, F11): a content-enter tells "the data arrived" - so it plays only the FIRST time a state
    /// block appears on screen (first loading/empty/error -> content). A reload of content already shown (a History range
    /// change: loading -> charts again, a refresh) is instant: re-fading the metric cards would suggest new information.
    /// </summary>
    public static bool PlaysContentEnter(bool becameVisible, bool isLive, bool reducedMotion, bool shownBefore) =>
        PlaysEnter(hasEnter: true, becameVisible, isLive, reducedMotion) && !shownBefore;
}
