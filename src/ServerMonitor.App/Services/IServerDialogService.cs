using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Services;

/// <summary>
/// UI.7 B-3: only the non-editor server dialog. Add / Edit / discovery / SSH import open the editor PAGE through
/// <see cref="IServerEditorSession"/>.
/// </summary>
public interface IServerDialogService
{
    Task<bool> ConfirmRemoveAsync(Server server);
}
