using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Services;

public interface IServerDialogService
{
    Task<ServerInput?> ShowEditorAsync(Server? server);

    Task<bool> ConfirmRemoveAsync(Server server);
}
