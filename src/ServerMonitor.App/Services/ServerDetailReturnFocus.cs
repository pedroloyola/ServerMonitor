namespace ServerMonitor.App.Services;

/// <summary>Which Server Detail control opened the page the user is coming back from (UI.5 a11y debt from UI.4).</summary>
public enum ServerDetailReturnTarget
{
    None,
    History,
    Workloads
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
    private (Guid ServerId, ServerDetailReturnTarget Target) _pending;

    public void Remember(Guid serverId, ServerDetailReturnTarget target)
    {
        lock (_gate)
        {
            _pending = (serverId, target);
        }
    }

    /// <summary>The remembered target for <paramref name="serverId"/>, at most once; another server's memory is dropped.</summary>
    public ServerDetailReturnTarget Take(Guid serverId)
    {
        lock (_gate)
        {
            var pending = _pending;
            _pending = (Guid.Empty, ServerDetailReturnTarget.None);
            return pending.ServerId == serverId ? pending.Target : ServerDetailReturnTarget.None;
        }
    }
}
