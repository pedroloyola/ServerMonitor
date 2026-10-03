namespace ServerMonitor.App.Services;

/// <summary>Where the Server Detail page (D-UI4-DETAIL, UI.5) was opened from; its breadcrumb returns there.</summary>
public enum ServerDetailOrigin
{
    /// <summary>The Visão geral (summary list, priority problem, or a widget "open server" deep-link).</summary>
    Overview,

    /// <summary>The Servidores directory page.</summary>
    Servers
}
