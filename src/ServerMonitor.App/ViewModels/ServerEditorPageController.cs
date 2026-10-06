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
    // UI.7B: "Voltar ao formulário" was pressed on the mismatch on screen; cleared when the view model's mismatch goes.
    private bool _mismatchAcknowledged;

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

    /// <summary>
    /// UI.7A fix c1 (Cortex M-1): a Save is being persisted. The page stays until the session decides (success → Detail,
    /// failure → the notice): the exit guard refuses without asking, and Cancelar / Voltar / Esc do nothing meanwhile.
    /// </summary>
    public bool IsSaving => _saving;

    /// <summary>Raised when <see cref="IsSaving"/> changes (the page disables Cancelar, the header button and the actions).</summary>
    public event EventHandler? SavingChanged;

    public bool Load(ServerEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
        ViewModel = _session.Attach(request);
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        return ViewModel is not null;
    }

    /// <summary>
    /// H-UI7-3, the page's exit guard: clean (or saved, or already gone) → leave at once; dirty → "Descartar alterações?"
    /// (default: keep editing). Never saves. A test still running ends with the view model when the page goes.
    /// </summary>
    public Task<bool> ConfirmLeaveAsync()
    {
        // M-1: a Save in flight decides where the page goes; leaving now would ask about (or discard) a committed edit.
        // The Save's OWN navigation to Detail comes after the visit is marked saved, so it passes.
        if (!_disposed && _saving && Request is not null && !_session.IsSaved(Request))
        {
            return Task.FromResult(false);
        }

        if (_disposed || Request is null || ViewModel is null || _session.IsSaved(Request))
        {
            return Task.FromResult(true);
        }

        // A clean form leaves at once: the view model's Dispose cancels a running test with nothing after it (B-7).
        if (!IsDirty)
        {
            return Task.FromResult(true);
        }

        // Cortex §8: about to ask with a test running - also the retest after an accepted key in the layer - cancels that
        // test first, so nothing runs unowned under the question. The layer itself stays: the question opens over it, and
        // "Continuar a editar" finds it as it was (nothing lost silently).
        if (ViewModel.IsTestingConnection)
        {
            ViewModel.CancelTest();
        }

        return _discardPrompt.ConfirmDiscardAsync(new ServerEditorDiscardContext(
            Request.Mode,
            ViewModel.OpenedName,
            ViewModel.Name));
    }

    /// <summary>Cancelar / Voltar ao detalhe: back to the origin through the exit guard.</summary>
    public void Cancel()
    {
        if (!_disposed && !_saving && Request is not null)
        {
            _session.Leave(Request);
        }
    }

    /// <summary>B-20: Esc = Back through the guard; a running test is cancelled first.</summary>
    public void Escape()
    {
        if (_saving)
        {
            return;
        }

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

        SetSaving(true);
        ServerEditorSaveOutcome saved;
        try
        {
            saved = await _session.SaveAsync(Request, result!);
        }
        finally
        {
            SetSaving(false);
        }

        if (saved.Status == ServerEditorSaveStatus.NotCurrent && !_disposed)
        {
            // M-1 (3): a visit that already saved but is still on screen is never left orphaned: go where its Save goes.
            _session.ResumeSavedDestination(Request);
        }

        return new ServerEditorSubmitOutcome(saved.Status, saved.SecretsConsumed);
    }

    /// <summary>
    /// UI.7B (B-10): the trust prompt the page's dialog host should show now, or null. Showing never accepts: a key is
    /// trusted only by <see cref="AcceptTrustAsync"/>, an explicit gesture distinct from Save (no TOFU, no trust on save).
    /// </summary>
    public HostKeyTrustPrompt? PromptToShow() =>
        _disposed || ViewModel is not { } viewModel ? null : HostKeyTrustPrompt.From(viewModel);

    /// <summary>
    /// "Confiar e testar / continuar": the SAME <see cref="ServerEditorViewModel.TrustAndConnectAsync"/>, and only while
    /// the key pending in the view model is still exactly the one the dialog showed (same hop, subject and fingerprint).
    /// A mismatch is never accepted. False = nothing was trusted from here.
    /// </summary>
    public async Task<bool> AcceptTrustAsync(HostKeyTrustPrompt shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        if (_disposed
            || !shown.CanAccept
            || ViewModel is not { } viewModel
            || viewModel.IsConnectionWorkInProgress
            || PromptToShow() != shown)
        {
            return false;
        }

        await viewModel.TrustAndConnectAsync();
        return true;
    }

    /// <summary>
    /// UI.7B: the in-page modal layer the view model needs now (Cortex §8: an in-tree layer, never a ContentDialog). The
    /// import while its dialog is open; the trust prompt while one is shown (and not acknowledged) or while an accepted
    /// key is written and retested.
    /// </summary>
    public ServerEditorLayer LayerToShow(bool accepting)
    {
        if (_disposed || ViewModel is not { } viewModel)
        {
            return ServerEditorLayer.None;
        }

        if (viewModel.IsSshConfigImportOpen)
        {
            return ServerEditorLayer.Import;
        }

        if (accepting)
        {
            return ServerEditorLayer.Trust;
        }

        return PromptToShow() is { } prompt && !(prompt.Kind == HostKeyPromptKind.Mismatch && _mismatchAcknowledged)
            ? ServerEditorLayer.Trust
            : ServerEditorLayer.None;
    }

    /// <summary>
    /// Cancelar / Voltar ao formulário / Esc on the layer: during an accept the retest is cancelled (the key already being
    /// written completes - it was accepted); an unknown key is dismissed (nothing written); a mismatch is acknowledged
    /// (never accepted); the import is closed (its load cancelled).
    /// </summary>
    public void CloseLayer(ServerEditorLayer layer, bool accepting)
    {
        if (_disposed || ViewModel is not { } viewModel)
        {
            return;
        }

        switch (layer)
        {
            case ServerEditorLayer.Trust when accepting:
                if (viewModel.IsTestingConnection)
                {
                    viewModel.CancelTest();
                }

                break;
            case ServerEditorLayer.Trust:
                if (viewModel.HasHostKeyMismatch)
                {
                    _mismatchAcknowledged = true;
                }

                DismissTrustPrompt();
                break;
            case ServerEditorLayer.Import:
                CloseImport();
                break;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerEditorViewModel.HasHostKeyMismatch) && ViewModel is { HasHostKeyMismatch: false })
        {
            _mismatchAcknowledged = false;
        }
    }

    /// <summary>Cancel / Esc / close on an unknown key: the prompt is dropped and nothing is written.</summary>
    public void DismissTrustPrompt()
    {
        if (!_disposed && ViewModel is { HasUnknownHostKey: true } viewModel)
        {
            viewModel.DismissHostKeyPrompt();
        }
    }

    /// <summary>"Importar de SSH" (Add only): the existing read-only load; the dialog follows the view model's state.</summary>
    public Task OpenImportAsync() =>
        _disposed || ViewModel is not { IsSshConfigImportAvailable: true } viewModel
            ? Task.CompletedTask
            : viewModel.LoadSshConfigHostsAsync();

    /// <summary>"Usar perfil": the existing Add-only apply, which never sets auth, a secret or a trust. Blocked = refused.</summary>
    public bool ApplyImport(SshConfigHostOptionViewModel? option) =>
        !_disposed
        && SshConfigImportPresentation.CanUse(option)
        && ViewModel is { } viewModel
        && viewModel.ApplySshConfigHost(option!);

    /// <summary>Closing the import dialog cancels a load still running (and leaves an applied profile's message alone).</summary>
    public void CloseImport()
    {
        if (!_disposed && ViewModel is { IsSshConfigImportOpen: true } viewModel)
        {
            viewModel.CloseSshConfigImport();
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

    private void SetSaving(bool value)
    {
        if (_saving != value)
        {
            _saving = value;
            SavingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The visit ends with its page (idempotent): the session disposes the view model.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (Request is not null)
        {
            _session.Detach(Request);
        }
    }
}

/// <summary>UI.7B: the editor page's one in-page modal layer (none, the trust prompt / 7C test, or "Importar de SSH").</summary>
public enum ServerEditorLayer
{
    None,
    Trust,
    Import
}

/// <summary>What a submit attempt did. <see cref="Invalid"/>: the form did not validate (nothing persisted).</summary>
public sealed record ServerEditorSubmitOutcome(ServerEditorSaveStatus? Status, bool SecretsConsumed = false)
{
    public static ServerEditorSubmitOutcome Invalid { get; } = new((ServerEditorSaveStatus?)null);

    public bool IsInvalid => Status is null;
}
