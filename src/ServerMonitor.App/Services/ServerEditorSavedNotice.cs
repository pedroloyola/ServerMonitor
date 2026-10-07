namespace ServerMonitor.App.Services;

/// <summary>What the server editor saved before it went to the server's Detail page (UI.7 B-5).</summary>
public sealed record ServerEditorSaved(ServerEditorMode Mode, Guid ServerId, string ServerName);

/// <summary>
/// UI.7 B-5 (Figma 12 112:19456 / 112:19787): the one-shot "Servidor adicionado" / "Alterações guardadas" toast that a
/// saved editor visit hands to the Detail page it navigates to. Same shape as <see cref="ServersReturnNotice"/>: one slot,
/// posted by the session right before the saved navigation, taken (and cleared) once by the Detail of THAT server - a
/// Detail of another server never shows it, and a later visit never shows it again. Not a global toast system.
/// </summary>
public sealed class ServerEditorSavedNotice
{
    private readonly Lock _gate = new();
    private ServerEditorSaved? _pending;

    public void Post(ServerEditorSaved saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        lock (_gate)
        {
            _pending = saved;
        }
    }

    /// <summary>Test probe: a notice is waiting (for any server).</summary>
    internal bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null;
            }
        }
    }

    /// <summary>The pending notice for <paramref name="serverId"/> at most once; a notice for another server is dropped.</summary>
    public ServerEditorSaved? TakeFor(Guid serverId)
    {
        lock (_gate)
        {
            var saved = _pending;
            _pending = null;
            return saved?.ServerId == serverId ? saved : null;
        }
    }
}
