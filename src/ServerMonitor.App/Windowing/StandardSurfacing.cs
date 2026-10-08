namespace ServerMonitor.App.Windowing;

/// <summary>
/// UI.8 D-UI8-10 / Cortex RC-4 §4: the ONE sequence that surfaces the window in Standard for a destination that only
/// exists there (a server's Detail, Settings, Settings › Background, the editor). The defect it fixes: the mode switch
/// used to run BEFORE the window was materialized, when the coordinator is not initialized yet and ignores it - so a
/// headless process with Compact persisted opened in Compact and navigated behind it. Here the switch always runs AFTER
/// <c>tryMaterialize</c> (which constructs the window and so initializes the coordinator with the persisted mode).
/// Pure and WinUI-free; every step is injected.
/// </summary>
internal static class StandardSurfacing
{
    /// <summary>
    /// Contract: materialize → (navigateWhileHidden) → switch → show; except a minimized window, which is shown (restored)
    /// first and switched after - the order ToggleCompactMode already proves, because applying bounds to a minimized
    /// window is unreliable. A hidden or never-shown window switches before it is shown, so no Compact frame is painted.
    /// Returns false (and does nothing else) when no window exists and none can be created, and false when the coordinator
    /// did not end in Standard after the switch (Cortex 8B gate N-1: the original defect's class - an ignored switch - must
    /// never read as success; the window is still shown, so the user is not left with nothing).
    /// </summary>
    public static bool Run(
        Func<bool> tryMaterialize,
        IWindowModeCoordinator mode,
        Func<bool> isMinimized,
        Action show,
        Action? navigateWhileHidden = null)
    {
        ArgumentNullException.ThrowIfNull(tryMaterialize);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(isMinimized);
        ArgumentNullException.ThrowIfNull(show);

        if (!tryMaterialize())
        {
            return false;
        }

        navigateWhileHidden?.Invoke();
        if (isMinimized())
        {
            show();
            mode.SwitchTo(WindowMode.Standard);
            return mode.CurrentMode == WindowMode.Standard;
        }

        mode.SwitchTo(WindowMode.Standard);
        show();
        return mode.CurrentMode == WindowMode.Standard;
    }
}
