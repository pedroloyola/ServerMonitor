namespace ServerMonitor.App.Services;

/// <summary>Where the interim server page (D-UI4-DETAIL) was opened from; its breadcrumb returns there.</summary>
public enum ServerDetailOrigin
{
    /// <summary>The Visão geral (summary list, priority problem, or a widget "open server" deep-link).</summary>
    Overview,

    /// <summary>The Servidores directory page.</summary>
    Servers
}
