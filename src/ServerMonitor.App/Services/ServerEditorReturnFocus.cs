namespace ServerMonitor.App.Services;

/// <summary>
/// UI.7 B-5: where keyboard focus returns when the user leaves the editor page without saving - the control that opened
/// it, by name, on the origin page (Visão geral, Servidores, Detalhe). Remembered by the session as it leaves, taken ONCE
/// by the next origin page of that destination (pages are per visit, so the memory cannot live in them). A control that
/// is gone (or a templated one with no page-level name) falls back to the page heading.
/// </summary>
public sealed class ServerEditorReturnFocus
{
    private readonly Lock _gate = new();
    private (NavigationDestination? Destination, string? ElementName) _pending;

    public void Remember(NavigationDestination destination, string? elementName)
    {
        lock (_gate)
        {
            _pending = (destination, string.IsNullOrWhiteSpace(elementName) ? null : elementName);
        }
    }

    /// <summary>The remembered control name for <paramref name="destination"/>, at most once; another page's memory is dropped.</summary>
    public string? Take(NavigationDestination destination)
    {
        lock (_gate)
        {
            var pending = _pending;
            _pending = (null, null);
            return pending.Destination == destination ? pending.ElementName : null;
        }
    }
}
