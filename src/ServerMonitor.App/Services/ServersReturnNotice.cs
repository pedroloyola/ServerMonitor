namespace ServerMonitor.App.Services;

/// <summary>What the Server Detail page did before it returned to Servidores (H-UI5-1).</summary>
public enum ServersNoticeKind
{
    Hidden,
    Removed
}

/// <summary>One transient notice for the Servidores page: the action and the server it was about.</summary>
public sealed record ServersNotice(ServersNoticeKind Kind, string ServerName);

/// <summary>
/// UI.5 Boss B2 answer 1: the one-shot notice ("Servidor ocultado" / "Servidor removido") that the Server Detail page
/// hands to the NEXT Servidores page through the navigation return. Not a global toast system: one slot, posted by the
/// Detail that left because its own Ocultar/Remover succeeded, taken (and cleared) once by the Servidores page.
/// </summary>
public sealed class ServersReturnNotice
{
    private readonly Lock _gate = new();
    private ServersNotice? _pending;

    public void Post(ServersNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        lock (_gate)
        {
            _pending = notice;
        }
    }

    /// <summary>The pending notice at most once.</summary>
    public ServersNotice? Take()
    {
        lock (_gate)
        {
            var notice = _pending;
            _pending = null;
            return notice;
        }
    }
}
