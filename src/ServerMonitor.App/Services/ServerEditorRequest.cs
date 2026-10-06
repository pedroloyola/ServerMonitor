using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Services;

/// <summary>UI.7 B-1: what the editor page is for. An edit only ever comes from the Server Detail page.</summary>
public enum ServerEditorMode
{
    Add,
    Edit
}

/// <summary>
/// UI.7 B-5: the page the editor was opened from; Cancel / Back / Esc return there. <see cref="ServerId"/> is set only for
/// <see cref="NavigationDestination.Detail"/>.
/// </summary>
public sealed record ServerEditorOrigin(NavigationDestination Destination, Guid ServerId = default)
{
    public static ServerEditorOrigin Overview { get; } = new(NavigationDestination.Overview);
}

/// <summary>
/// UI.7 B-1: one visit of the editor page. Immutable and compared by REFERENCE: every open is a new request, so a page can
/// only ever reach the view model of its own visit, never one of an earlier (or later) open.
/// </summary>
public sealed class ServerEditorRequest
{
    internal ServerEditorRequest(
        ServerEditorMode mode,
        Server? existing,
        ServerDiscoveryPrefill? prefill,
        bool openSshImport,
        ServerEditorOrigin origin)
    {
        if ((mode == ServerEditorMode.Edit) != existing is not null)
        {
            throw new ArgumentException("An edit names the server it edits; an add never does.", nameof(existing));
        }

        Mode = mode;
        Existing = existing;
        Prefill = prefill;
        OpenSshImport = openSshImport;
        Origin = origin;
    }

    public ServerEditorMode Mode { get; }

    /// <summary>The saved server an edit starts from; null for an add.</summary>
    public Server? Existing { get; }

    /// <summary>A network-discovery suggestion that seeds an add (never an edit).</summary>
    public ServerDiscoveryPrefill? Prefill { get; }

    /// <summary>The add opens with the read-only <c>~/.ssh/config</c> import already loading (M14.5 "Importar de SSH").</summary>
    public bool OpenSshImport { get; }

    public ServerEditorOrigin Origin { get; }
}
