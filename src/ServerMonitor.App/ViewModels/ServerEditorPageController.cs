using ServerMonitor.App.Services;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// UI.7: everything the editor page decides that is not drawing - its exit guard, Cancel/Esc, the single submit path and
/// the action hint - kept out of the XAML code-behind so it is testable with the real session, navigation and view model.
/// The page owns one controller per visit and disposes it when navigation replaces the page.
/// </summary>
public sealed class ServerEditorPageController : IDisposable
{
    private readonly IServerEditorSession _session;
    private readonly IServerEditorDiscardPrompt _discardPrompt;
    private bool _saving;
    private bool _disposed;

    public ServerEditorPageController(IServerEditorSession session, IServerEditorDiscardPrompt discardPrompt)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _discardPrompt = discardPrompt ?? throw new ArgumentNullException(nameof(discardPrompt));
    }

    public ServerEditorRequest? Request { get; private set; }

    /// <summary>The visit's form view model (owned by the session); null when the visit had already ended.</summary>
    public ServerEditorViewModel? ViewModel { get; private set; }

    public bool IsEdit => Request?.Mode == ServerEditorMode.Edit;

    /// <summary>
    /// Text typed into a password box that has not reached the view model yet (it is only read on Test/Save). The page
    /// wires it; a form with an unsent secret is not clean.
    /// </summary>
    public Func<bool>? HasTypedSecret { get; set; }

    public bool IsDirty => ViewModel?.IsDirty == true || HasTypedSecret?.Invoke() == true;

    public bool Load(ServerEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
        ViewModel = _session.Attach(request);
        return ViewModel is not null;
    }

    /// <summary>
    /// H-UI7-3, the page's exit guard: clean (or saved, or already gone) → leave at once; dirty → "Descartar alterações?"
    /// (default: keep editing). Never saves. A test still running ends with the view model when the page goes.
    /// </summary>
    public Task<bool> ConfirmLeaveAsync()
    {
        if (_disposed || Request is null || ViewModel is null || _session.IsSaved(Request) || !IsDirty)
        {
            return Task.FromResult(true);
        }

        return _discardPrompt.ConfirmDiscardAsync(new ServerEditorDiscardContext(
            Request.Mode,
            ViewModel.OpenedName,
            ViewModel.Name));
    }

    /// <summary>Cancelar / Voltar ao detalhe: back to the origin through the exit guard.</summary>
    public void Cancel()
    {
        if (!_disposed && Request is not null)
        {
            _session.Leave(Request);
        }
    }

    /// <summary>B-20: Esc = Back through the guard; a running test is cancelled first.</summary>
    public void Escape()
    {
        if (ViewModel?.IsTestingConnection == true)
        {
            ViewModel.CancelTest();
        }

        Cancel();
    }

    /// <summary>
    /// The ONE submit path (button and Enter): never while a test or a trust write runs (Vigil H7), secrets captured from
    /// the password boxes just before <see cref="ServerEditorViewModel.TryCreateResult"/>, persisted only by the session
    /// through the opener's write path. Null = nothing was attempted.
    /// </summary>
    public async Task<ServerEditorSubmitOutcome?> SubmitAsync(Action captureSecrets)
    {
        ArgumentNullException.ThrowIfNull(captureSecrets);
        if (_disposed || _saving || Request is null || ViewModel is not { } viewModel || viewModel.IsConnectionWorkInProgress)
        {
            return null;
        }

        captureSecrets();
        if (!viewModel.TryCreateResult(out var result))
        {
            return ServerEditorSubmitOutcome.Invalid;
        }

        _saving = true;
        try
        {
            var saved = await _session.SaveAsync(Request, result!);
            return new ServerEditorSubmitOutcome(saved.Status, saved.SecretsConsumed);
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>
    /// The action-bar hint (Figma 04/05, advice only - nothing is gated by a test, G-10): unsaved edits are named; an add
    /// without host or user asks for them; otherwise "test before saving".
    /// </summary>
    public string ActionHintKey()
    {
        if (ViewModel is not { } viewModel)
        {
            return string.Empty;
        }

        if (IsEdit)
        {
            return IsDirty ? "ServerEditorHintUnsaved" : "ServerEditorHintTestBeforeSave";
        }

        return string.IsNullOrWhiteSpace(viewModel.Host) || string.IsNullOrWhiteSpace(viewModel.Username)
            ? "ServerEditorHintFillToTest"
            : "ServerEditorHintTestBeforeSave";
    }

    /// <summary>The visit ends with its page (idempotent): the session disposes the view model.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Request is not null)
        {
            _session.Detach(Request);
        }
    }
}

/// <summary>What a submit attempt did. <see cref="Invalid"/>: the form did not validate (nothing persisted).</summary>
public sealed record ServerEditorSubmitOutcome(ServerEditorSaveStatus? Status, bool SecretsConsumed = false)
{
    public static ServerEditorSubmitOutcome Invalid { get; } = new((ServerEditorSaveStatus?)null);

    public bool IsInvalid => Status is null;
}
