namespace ServerMonitor.App.Services;

/// <summary>
/// UI.5 §4: where <see cref="INavigationService.GoToSettings(SettingsSection)"/> lands. Settings is two sub-pages until
/// UI.6 brings the sidebar: "Definições" (General) and "Dados e servidores" (Data). Moving between them is an in-page
/// link, never a second navigation architecture (Cortex 2).
/// </summary>
public enum SettingsSection
{
    /// <summary>"Definições": appearance, language, background monitoring, notifications, window.</summary>
    General,

    /// <summary>"Dados e servidores": hidden servers, ignored devices, history, backup/restore, about.</summary>
    Data,

    /// <summary>The Data page with its About card brought into view (H-UI5-4: the "Sobre" disclosure in General).</summary>
    About
}

/// <summary>
/// A singleton Settings sub-page that must react when navigation targets it while it is ALREADY the frame content
/// (Cortex #6): such a navigation swaps nothing, so the page gets no Loaded and a pending section request (Background,
/// About) would otherwise wait until the next visit.
/// <para>
/// Cortex B1 M-1: only a LOADED page is told. Just after a cold <c>GoToSettings</c> the page is the content but its Loaded
/// has not run yet; telling it then would consume the request on an element that cannot scroll, and Loaded would find it
/// gone. While not loaded, the request stays pending and Loaded consumes it (the M13 S2 §D.1 path).
/// </para>
/// </summary>
public interface ISettingsNavigationTarget
{
    /// <summary>True once the page has run Loaded and is in the live tree (and false again after Unloaded).</summary>
    bool IsReadyForSectionRequest { get; }

    void OnNavigatedToAgain();
}
