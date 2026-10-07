using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Services;

/// <summary>
/// UI.7 B-4: how the opener persists a result. The write path stays the caller's (DashboardViewModel →
/// IServerProfileService); the session only waits for its answer before it leaves the page.
/// </summary>
public delegate Task<ServerOperationResult> ServerEditorPersist(ServerEditorResult result);

public enum ServerEditorSaveStatus
{
    /// <summary>Persisted; the session has navigated to the server's Detail page.</summary>
    Saved,

    /// <summary>Not persisted (validation, ServerNotFound, a store failure): the page stays with the form.</summary>
    Failed,

    /// <summary>A restore holds the configuration (M14.6): nothing was written; retrying cannot help until restart.</summary>
    ConfigurationLocked,

    /// <summary>The page is not the live visit any more (already left); nothing was persisted.</summary>
    NotCurrent
}

/// <param name="SecretsConsumed">The result carried a typed secret, which the editor gave up; a retry needs it typed again.</param>
public sealed record ServerEditorSaveOutcome(ServerEditorSaveStatus Status, bool SecretsConsumed = false);

/// <summary>
/// UI.7 B-3: THE owner of the server editor's lifetime (replaces <c>ServerDialogService.ShowEditor*</c>). Exactly one
/// live editor: opening another leaves the current page through the exit guard (H-UI7-3), which disposes the old view
/// model before the new one exists. Every visit ends in <see cref="Detach"/>, whatever the exit, so its view model is
/// disposed exactly once.
/// </summary>
public interface IServerEditorSession
{
    /// <summary>Add. The task completes when the visit ends (saved, cancelled, left or refused).</summary>
    Task OpenAddAsync(ServerEditorPersist persist);

    /// <summary>Edit of <paramref name="server"/> (from its Detail page).</summary>
    Task OpenEditAsync(Server server, ServerEditorPersist persist);

    /// <summary>Add seeded with a discovery suggestion (name/host/port only).</summary>
    Task OpenDiscoveryAsync(ServerDiscoveryPrefill prefill, ServerEditorPersist persist);

    /// <summary>Add with the read-only SSH config import already loading.</summary>
    Task OpenSshImportAsync(ServerEditorPersist persist);

    /// <summary>The page of <paramref name="request"/> asks for its view model; null when that visit already ended.</summary>
    ServerEditorViewModel? Attach(ServerEditorRequest request);

    /// <summary>
    /// Persists through the opener's <see cref="ServerEditorPersist"/> and disposes <paramref name="result"/>. On success it
    /// navigates to the server's Detail page; on failure the page stays.
    /// </summary>
    Task<ServerEditorSaveOutcome> SaveAsync(ServerEditorRequest request, ServerEditorResult result);

    /// <summary>
    /// UI.7C (H-UI7-2): every saved server, visible and hidden, for the in-memory duplicate notice (read only; empty when
    /// the list cannot be read - the notice is advice, never a gate).
    /// </summary>
    Task<IReadOnlyList<Server>> GetKnownServersAsync();

    /// <summary>"Abrir servidor" on the duplicate notice: that server's Detail page, through the exit guard (H-UI7-3).</summary>
    void OpenExistingServer(ServerEditorRequest request, Guid serverId);

    /// <summary>True once the visit saved (its page may then leave without asking) or ended.</summary>
    bool IsSaved(ServerEditorRequest request);

    /// <summary>
    /// UI.7A fix c1 (Cortex M-1 (3)): a visit that saved but whose page is still on screen (its navigation was not taken)
    /// goes where its Save goes - the saved server's Detail. No effect for a visit that did not save or already ended.
    /// </summary>
    void ResumeSavedDestination(ServerEditorRequest request);

    /// <summary>Cancel / Back / Esc: back to the origin, through the exit guard; focus returns to the opening control.</summary>
    void Leave(ServerEditorRequest request);

    /// <summary>The page is gone (replaced by navigation): the visit ends and its view model is disposed. Idempotent.</summary>
    void Detach(ServerEditorRequest request);
}
