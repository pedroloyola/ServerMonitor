namespace ServerMonitor.App.Services;

/// <summary>
/// UI.7 H-UI7-3: the page currently shown may hold the user back from leaving it. <see cref="NavigationService"/> asks it
/// before EVERY navigation away (sidebar, breadcrumb, Back, Cancel, Esc, "Abrir servidor", a second editor, an external
/// activation), so there is one exit path. Implemented by the page that is the navigation content; nothing registers it.
/// </summary>
public interface INavigationExitGuard
{
    /// <summary>
    /// True = leave now. A page with nothing to lose answers synchronously (an already completed task), so a clean exit
    /// navigates at once; otherwise it asks the user. Never saves anything.
    /// </summary>
    Task<bool> ConfirmLeaveAsync();
}
