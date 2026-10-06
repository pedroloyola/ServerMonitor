using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Backup;
using Windows.System;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.7A: the Add / Edit server page. Code-behind is drawing and keys only; every decision (exit guard, Cancel/Esc, the
/// single submit path, the hint) lives in <see cref="ServerEditorPageController"/> over the session-owned view model.
/// A fresh page per visit: navigation disposes it when another page replaces it, which ends the visit (the session
/// disposes the view model). Unloading alone (window hidden to the tray) keeps the editor and only clears the password
/// boxes (H-UI7-3, Vigil H5).
/// UI.7B: the page is also the editor's dialog host - at most ONE ContentDialog at a time, chosen from the view model's
/// state: the trust prompt (the connection dialog, which 7C reuses for the test) or "Importar de SSH". The dialogs draw;
/// the controller decides (accept = the SAME TrustAndConnectAsync, only for the key on screen; dismiss writes nothing).
/// </summary>
public sealed partial class ServerEditorPage : Page, IServerEditorView, INavigationExitGuard, IDisposable
{
    private readonly ServerEditorPageController _controller;
    private readonly ILocalizationService _localization;
    private ServerEditorViewModel? _viewModel;
    private bool _disposed;
    // UI.7B dialog host: the one dialog open now (null = none), completed when it has closed.
    private ContentDialog? _openDialog;
    private TaskCompletionSource? _dialogClosed;
    private bool _acceptingTrust;

    public ServerEditorPage(
        IServerEditorSession session,
        IServerEditorDiscardPrompt discardPrompt,
        ILocalizationService localization)
    {
        InitializeComponent();
        _localization = localization;
        _controller = new ServerEditorPageController(session, discardPrompt)
        {
            HasTypedSecret = () => ServerForm.HasTypedSecret()
        };
        // M-1: while a Save is persisted nothing on the page can leave or act (the session decides where it goes).
        _controller.SavingChanged += (_, _) => UpdateActionState();
        Loaded += OnLoaded;
        Unloaded += (_, _) => ServerForm.ClearSecrets();
        KeyDown += OnPageKeyDown;
    }

    public void Load(ServerEditorRequest request)
    {
        if (!_controller.Load(request) || _controller.ViewModel is not { } viewModel)
        {
            // The visit already ended (left while opening): nothing to edit.
            return;
        }

        _viewModel = viewModel;
        ServerForm.Configure(_localization, _controller.IsEdit);
        ServerForm.DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        var edit = _controller.IsEdit;
        EditorHeading.Text = _localization.GetString(edit ? "ServerEditorEditTitle" : "ServerEditorAddTitle");
        EditorSubtitle.Text = edit
            ? string.Format(CultureInfo.CurrentCulture, _localization.GetString("ServerEditorEditSubtitleFormat"), request.Existing!.Name)
            : _localization.GetString("ServerEditorAddSubtitle");
        PrimaryButton.Content = _localization.GetString(edit ? "ServerEditorSaveButton" : "ServerEditorAddButton");
        HeaderButton.Content = _localization.GetString(edit ? "ServerEditorBackToDetailButton" : "ServerEditorImportButton");
        HeaderButton.Visibility = edit || viewModel.IsSshConfigImportAvailable ? Visibility.Visible : Visibility.Collapsed;
        CredentialNoteTitle.Text = _localization.GetString(edit ? "ServerEditorCredentialNoteEdit" : "ServerEditorCredentialNoteAdd");
        UpdateActionState();
    }

    /// <summary>
    /// H-UI7-3 guard. A navigation that arrives while a dialog is open (external activation) first closes it - an open trust
    /// prompt is dismissed, nothing is trusted - so the "Descartar alterações?" question is never stacked on it.
    /// </summary>
    public async Task<bool> ConfirmLeaveAsync()
    {
        await CloseDialogAsync();
        return await _controller.ConfirmLeaveAsync();
    }

    /// <summary>Idempotent: ends the visit (the session disposes the view model) and clears the password boxes.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        ServerForm.ClearSecrets();
        _controller.Dispose();
        // The controller is gone first, so closing the dialog dismisses / applies nothing.
        _openDialog?.Hide();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // B-20: entering the page lands on the H1 (Nome is the first Tab stop after it).
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!ShellPageFocus.GetKeepSidebar(this))
            {
                ShellPageFocus.FocusHeading(this);
            }

            // An editor opened for "Importar de SSH" started its load before the page had a window.
            EvaluateDialogs();
        });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerEditorViewModel.IsConnectionWorkInProgress)
            or nameof(ServerEditorViewModel.IsTestingConnection)
            or nameof(ServerEditorViewModel.IsDirty)
            or nameof(ServerEditorViewModel.Host)
            or nameof(ServerEditorViewModel.Username))
        {
            UpdateActionState();
        }

        if (e.PropertyName is nameof(ServerEditorViewModel.HasUnknownHostKey)
            or nameof(ServerEditorViewModel.HasHostKeyMismatch)
            or nameof(ServerEditorViewModel.IsSshConfigImportOpen)
            or nameof(ServerEditorViewModel.IsLoadingSshConfig)
            or nameof(ServerEditorViewModel.SshConfigHosts)
            or nameof(ServerEditorViewModel.SshConfigLoadOutcome)
            or nameof(ServerEditorViewModel.SshConfigFileWarningMessage))
        {
            EvaluateDialogs();
        }
    }

    // The dialog host: what the view model shows now decides which ONE dialog is open. Never accepts or applies anything.
    private void EvaluateDialogs()
    {
        if (_disposed || _viewModel is not { } viewModel || XamlRoot is null)
        {
            return;
        }

        switch (_openDialog)
        {
            case SshConfigImportDialog import:
                if (viewModel.IsSshConfigImportOpen)
                {
                    import.Update(viewModel);
                }
                else
                {
                    import.Hide();
                }

                return;
            case ServerEditorConnectionDialog connection:
                if (_acceptingTrust)
                {
                    return; // the accept path redraws when the retest is over
                }

                if (_controller.PromptToShow() is { } prompt)
                {
                    if (prompt != connection.Prompt)
                    {
                        connection.ShowPrompt(prompt);
                    }
                }
                else
                {
                    connection.Hide();
                }

                return;
        }

        if (viewModel.IsSshConfigImportOpen)
        {
            var import = new SshConfigImportDialog(_localization);
            import.Update(viewModel);
            import.PrimaryButtonClick += OnImportPrimaryClick;
            import.Closing += (_, _) => _controller.CloseImport();
            _ = ShowDialogAsync(import);
        }
        else if (_controller.PromptToShow() is { } prompt)
        {
            var connection = new ServerEditorConnectionDialog(_localization);
            connection.ShowPrompt(prompt);
            connection.PrimaryButtonClick += OnTrustPrimaryClick;
            connection.Closing += OnConnectionDialogClosing;
            _ = ShowDialogAsync(connection);
        }
    }

    private async Task ShowDialogAsync(ContentDialog dialog)
    {
        var closed = new TaskCompletionSource();
        _openDialog = dialog;
        _dialogClosed = closed;
        dialog.XamlRoot = XamlRoot;
        dialog.RequestedTheme = ActualTheme;
        if (dialog is ServerEditorConnectionDialog connection)
        {
            connection.FillWindow();
        }
        else if (dialog is SshConfigImportDialog import)
        {
            import.FillWindow();
        }

        var shown = false;
        dialog.Opened += (_, _) => shown = true;
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // Another ContentDialog holds the window: fail closed - drop the prompt / the import, nothing is applied.
        }
        finally
        {
            _openDialog = null;
            _dialogClosed = null;
            closed.TrySetResult();
        }

        if (!shown)
        {
            _controller.DismissTrustPrompt();
            _controller.CloseImport();
            return;
        }

        // A prompt that came up while this dialog was closing (e.g. "PASSO 2 DE 2") opens now.
        EvaluateDialogs();
    }

    private async Task CloseDialogAsync()
    {
        if (_openDialog is { } dialog && _dialogClosed is { } closed)
        {
            dialog.Hide();
            await closed.Task;
        }
    }

    // "Confiar e testar / continuar": an explicit gesture in the dialog, distinct from Save. The dialog stays open while
    // the key is written and the SAME retest runs, then shows the next prompt (PASSO 2) or closes.
    private async void OnTrustPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_acceptingTrust || sender is not ServerEditorConnectionDialog { Prompt: { CanAccept: true } shown } dialog)
        {
            return;
        }

        _acceptingTrust = true;
        dialog.SetWorking(true);
        try
        {
            await _controller.AcceptTrustAsync(shown);
        }
        finally
        {
            _acceptingTrust = false;
        }

        if (_disposed)
        {
            return;
        }

        if (!ReferenceEquals(_openDialog, dialog))
        {
            EvaluateDialogs(); // closed meanwhile: the next prompt (if any) gets its own dialog
            return;
        }

        if (_controller.PromptToShow() is { } next)
        {
            dialog.ShowPrompt(next);
        }
        else
        {
            dialog.Hide();
        }
    }

    // Cancelar / Voltar ao formulário / Esc / closed by the host: an unknown key is dismissed (nothing written); during an
    // accept, the retest is cancelled (a key already being written completes - it was accepted).
    private void OnConnectionDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_acceptingTrust)
        {
            if (_viewModel is { IsTestingConnection: true } viewModel)
            {
                viewModel.CancelTest();
            }

            return;
        }

        _controller.DismissTrustPrompt();
    }

    private void OnImportPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (sender is not SshConfigImportDialog import || !_controller.ApplyImport(import.Selected))
        {
            args.Cancel = true; // nothing selected, or a blocked profile: never used
        }
    }

    private void UpdateActionState()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var saving = _controller.IsSaving;
        var busy = viewModel.IsConnectionWorkInProgress || saving;
        TestButton.IsEnabled = !busy;
        PrimaryButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !saving;
        HeaderButton.IsEnabled = !saving && (!busy || _controller.IsEdit);
        CancelTestButton.Visibility = viewModel.IsTestingConnection ? Visibility.Visible : Visibility.Collapsed;
        TestingRing.IsActive = viewModel.IsTestingConnection;
        TestingRing.Visibility = CancelTestButton.Visibility;
        var hint = _controller.ActionHintKey();
        ActionHint.Text = hint.Length == 0 ? string.Empty : _localization.GetString(hint);
    }

    // B-20: Esc = Back through the guard (a running test is cancelled first); Enter = the default action, except inside a
    // multi-line box and while a test or a trust write runs. A focused button, combo box or the trust panel keeps its keys.
    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _controller.Escape();
        }
        else if (e.Key == VirtualKey.Enter && _viewModel is { IsConnectionWorkInProgress: false })
        {
            if (FocusManager.GetFocusedElement(XamlRoot) is not TextBox { AcceptsReturn: true })
            {
                e.Handled = true;
                Submit();
            }
        }
    }

    private async void OnTestClick(object sender, RoutedEventArgs e) => await ServerForm.TestConnectionAsync();

    private void OnCancelTestClick(object sender, RoutedEventArgs e) => _viewModel?.CancelTest();

    private void OnCancelClick(object sender, RoutedEventArgs e) => _controller.Cancel();

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => Submit();

    private async void OnHeaderButtonClick(object sender, RoutedEventArgs e)
    {
        if (_controller.IsEdit)
        {
            _controller.Cancel();
        }
        else
        {
            await _controller.OpenImportAsync();
        }
    }

    private async void Submit()
    {
        SaveFailedNotice.Visibility = Visibility.Collapsed;
        var outcome = await _controller.SubmitAsync(ServerForm.CaptureSecret);
        if (outcome is null || _disposed)
        {
            return;
        }

        if (outcome.IsInvalid)
        {
            ServerForm.FocusFirstField();
            return;
        }

        if (outcome.Status is ServerEditorSaveStatus.Failed or ServerEditorSaveStatus.ConfigurationLocked)
        {
            ShowSaveFailure(outcome);
        }
    }

    // B-4: the page stays with the form as typed; a secret the editor gave up must be typed again (said explicitly).
    private void ShowSaveFailure(ServerEditorSubmitOutcome outcome)
    {
        SaveFailedNotice.Title = _localization.GetString(_controller.IsEdit ? "ServerEditorSaveFailedTitleEdit" : "ServerEditorSaveFailedTitleAdd");
        var message = outcome.Status == ServerEditorSaveStatus.ConfigurationLocked
            ? _localization.GetString(BackupMessageKeys.ConfigurationLocked)
            : _localization.GetString("ServerEditorSaveFailedBody");
        if (outcome.SecretsConsumed)
        {
            message += " " + _localization.GetString("ServerEditorSaveFailedRetypeSecret");
        }

        SaveFailedNotice.Message = message;
        SaveFailedNotice.Visibility = Visibility.Visible;
        SaveFailedNotice.StartBringIntoView();
    }
}
