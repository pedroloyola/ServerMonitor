namespace ServerMonitor.App.Services;

/// <summary>Which Server Detail control opened the page the user is coming back from (UI.5 a11y debt from UI.4).</summary>
public enum ServerDetailReturnTarget
{
    None,
    History,
    Workloads,

    /// <summary>M16: the row of a contributed server page (which one: the page id taken with it).</summary>
    ServerPage
}

/// <summary>
/// UI.5: where keyboard focus returns when the user comes back to a Server Detail page from Histórico or Serviços e
/// containers. Remembered by the Detail page that opens them and taken ONCE by the next Detail page of the SAME server
/// (the page is per visit, so the memory cannot live in it). A singleton of its own instead of a field on
/// <c>DashboardViewModel</c>, whose runtime-free tests build it without a constructor (Cortex §6.1).
/// </summary>
public sealed class ServerDetailReturnFocus
{
    private readonly Lock _gate = new();
    private (Guid ServerId, ServerDetailReturnTarget Target, string? PageId) _pending;

    public void Remember(Guid serverId, ServerDetailReturnTarget target) => Remember(serverId, target, pageId: null);

    /// <summary>M16: <paramref name="pageId"/> names the contributed server page whose row opened it.</summary>
    public void Remember(Guid serverId, ServerDetailReturnTarget target, string? pageId)
    {
        lock (_gate)
        {
            _pending = (serverId, target, target == ServerDetailReturnTarget.ServerPage ? pageId : null);
        }
    }

    /// <summary>The remembered target for <paramref name="serverId"/>, at most once; another server's memory is dropped.</summary>
    public ServerDetailReturnTarget Take(Guid serverId) => Take(serverId, out _);

    /// <summary>As <see cref="Take(Guid)"/>, with the page id when the target is a contributed server page's row.</summary>
    public ServerDetailReturnTarget Take(Guid serverId, out string? pageId)
    {
        lock (_gate)
        {
            var pending = _pending;
            _pending = (Guid.Empty, ServerDetailReturnTarget.None, null);
            var mine = pending.ServerId == serverId;
            pageId = mine ? pending.PageId : null;
            return mine ? pending.Target : ServerDetailReturnTarget.None;
        }
    }
}
