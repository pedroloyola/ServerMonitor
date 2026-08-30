namespace ServerMonitor.App.Services;

/// <summary>
/// The tiny, pure state machine behind a widget "focus this server" deep-link (ADR-018 §H/§11/§18/§28).
/// A widget <c>openServer</c> request stores the opaque id; an <c>openDashboard</c> request clears it (a
/// newer dashboard intent must beat an older server intent, §M-3); a newer server request replaces an
/// older one. <see cref="TryResolve"/> is called each time the server list changes: if the pending id is
/// present it returns it and clears the request (focus once); otherwise it stays pending until the server
/// loads, and a removed server simply never resolves (safe fallback, §11). No UI, fully testable.
/// </summary>
public sealed class PendingServerFocus
{
    private Guid? _pending;

    /// <summary>Requests focusing a server (replaces any older pending request).</summary>
    public void Request(Guid serverId) => _pending = serverId;

    /// <summary>Clears any pending focus (a dashboard intent supersedes an older server intent).</summary>
    public void Clear() => _pending = null;

    /// <summary>True while a focus request is still waiting to resolve.</summary>
    public bool HasPending => _pending is not null;

    /// <summary>
    /// If the pending server is now among <paramref name="currentServerIds"/>, returns it and clears the
    /// request; otherwise returns null (stays pending, or nothing pending).
    /// </summary>
    public Guid? TryResolve(IReadOnlyCollection<Guid> currentServerIds)
    {
        ArgumentNullException.ThrowIfNull(currentServerIds);

        if (_pending is { } id && currentServerIds.Contains(id))
        {
            _pending = null;
            return id;
        }

        return null;
    }
}
