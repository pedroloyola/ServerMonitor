using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// The pre-UI.7 dialog doubles ("the editor answers with this result") kept as a script for the dashboard tests that only
/// care what the dashboard does with a result. UI.7 B-3 moved the editor to a page owned by <see cref="IServerEditorSession"/>.
/// </summary>
internal interface IEditorScript : IServerDialogService
{
    Task<ServerEditorResult?> ShowEditorAsync(Server? server);

    Task<ServerEditorResult?> ShowEditorForDiscoveryAsync(ServerDiscoveryPrefill prefill);

    Task<ServerEditorResult?> ShowEditorForSshImportAsync();
}

/// <summary>
/// An <see cref="IServerEditorSession"/> over an <see cref="IEditorScript"/>: each Open* asks the script, and a non-null
/// result is persisted through the opener's delegate exactly as the editor page's Save does, then disposed. A persist
/// failure stays with the (absent) page, as in production (B-4). The page-side members are not used by these tests.
/// </summary>
internal sealed class EditorScriptSession(IEditorScript script) : IServerEditorSession
{
    public List<Core.Domain.ServerOperationResult> Persisted { get; } = [];

    public Task OpenAddAsync(ServerEditorPersist persist) => RunAsync(script.ShowEditorAsync(null), persist);

    public Task OpenEditAsync(Server server, ServerEditorPersist persist) => RunAsync(script.ShowEditorAsync(server), persist);

    public Task OpenDiscoveryAsync(ServerDiscoveryPrefill prefill, ServerEditorPersist persist) =>
        RunAsync(script.ShowEditorForDiscoveryAsync(prefill), persist);

    public Task OpenSshImportAsync(ServerEditorPersist persist) => RunAsync(script.ShowEditorForSshImportAsync(), persist);

    private async Task RunAsync(Task<ServerEditorResult?> shown, ServerEditorPersist persist)
    {
        using var result = await shown;
        if (result is not null)
        {
            Persisted.Add(await persist(result));
        }
    }

    public ServerEditorViewModel? Attach(ServerEditorRequest request) => throw new NotSupportedException();

    public Task<ServerEditorSaveOutcome> SaveAsync(ServerEditorRequest request, ServerEditorResult result) => throw new NotSupportedException();

    public bool IsSaved(ServerEditorRequest request) => throw new NotSupportedException();

    public void Leave(ServerEditorRequest request) => throw new NotSupportedException();

    public void Detach(ServerEditorRequest request) => throw new NotSupportedException();
}

/// <summary>An editor session that is never opened (tests that do not exercise Add / Edit).</summary>
internal sealed class InertEditorSession : IServerEditorSession
{
    public int Opens { get; private set; }

    public Task OpenAddAsync(ServerEditorPersist persist) { Opens++; return Task.CompletedTask; }

    public Task OpenEditAsync(Server server, ServerEditorPersist persist) { Opens++; return Task.CompletedTask; }

    public Task OpenDiscoveryAsync(ServerDiscoveryPrefill prefill, ServerEditorPersist persist) { Opens++; return Task.CompletedTask; }

    public Task OpenSshImportAsync(ServerEditorPersist persist) { Opens++; return Task.CompletedTask; }

    public ServerEditorViewModel? Attach(ServerEditorRequest request) => null;

    public Task<ServerEditorSaveOutcome> SaveAsync(ServerEditorRequest request, ServerEditorResult result) =>
        Task.FromResult(new ServerEditorSaveOutcome(ServerEditorSaveStatus.NotCurrent));

    public bool IsSaved(ServerEditorRequest request) => true;

    public void Leave(ServerEditorRequest request)
    {
    }

    public void Detach(ServerEditorRequest request)
    {
    }
}
