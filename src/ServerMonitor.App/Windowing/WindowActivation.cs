namespace ServerMonitor.App.Windowing;

/// <summary>
/// UI.12 COMPACT-EXIT-MAXIMIZE (Cortex C3-1): the "bring it back" block every surfacing path shares (tray "Abrir", the
/// Standard exits from Compact, the tray Compact toggle). It used to force <c>WindowState = Normal</c> unconditionally,
/// which un-maximized a maximized window - including the Standard window the coordinator had just maximized again on the
/// way out of Compact. Only a MINIMIZED window is restored now (back to whatever it was before it was minimized); a normal
/// or maximized one keeps its state. Pure and WinUI-free; every step is injected.
/// </summary>
internal static class WindowActivation
{
    /// <summary>
    /// Contract: show → (restore, only if still minimized once shown) → activate. Minimized is read AFTER showing, at the
    /// point the unconditional restore used to run, so a window that showing alone already brought back is not restored
    /// again (a second restore of a maximized window would un-maximize it).
    /// </summary>
    public static void ShowAndActivate(Action show, Func<bool> isMinimized, Action restore, Action activate)
    {
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(isMinimized);
        ArgumentNullException.ThrowIfNull(restore);
        ArgumentNullException.ThrowIfNull(activate);

        show();
        if (isMinimized())
        {
            restore();
        }

        activate();
    }
}
