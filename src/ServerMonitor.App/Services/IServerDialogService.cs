using ServerMonitor.Core.Models;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Services;

public interface IServerDialogService
{
    Task<ServerEditorResult?> ShowEditorAsync(Server? server);

    /// <summary>
    /// Opens the editor as a normal add, pre-filled from a network-discovery suggestion. It is an
    /// add (never an edit): the exact M3 test-connection / host-key-trust / save flow applies and
    /// only the non-sensitive name/host/port are seeded.
    /// </summary>
    Task<ServerEditorResult?> ShowEditorForDiscoveryAsync(ServerDiscoveryPrefill prefill);

    Task<bool> ConfirmRemoveAsync(Server server);
}
