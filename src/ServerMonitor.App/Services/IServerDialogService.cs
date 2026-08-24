using ServerMonitor.Core.Models;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Services;

public interface IServerDialogService
{
    Task<ServerEditorResult?> ShowEditorAsync(Server? server);

    Task<bool> ConfirmRemoveAsync(Server server);
}
