using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
/// UI.7B (Cortex §8, binding): the page hosts the editor's ONE in-page modal layer - in the tree, NOT a ContentDialog, so
/// the exit guard's "Descartar alterações?" can always open over it - showing what the view model needs now (the trust
/// prompt, which 7C reuses for the test, or "Importar de SSH"). The panels draw; the controller decides (accept = the SAME
/// TrustAndConnectAsync, only for the key on screen; dismiss writes nothing).
/// </summary>
public sealed partial class ServerEditorPage : Page, IServerEditorView, INavigationExitGuard, IDisposable
{
    private readonly ServerEditorPageController _controller;
    private readonly ILocalizationService _localization;
    private ServerEditorViewModel? _viewModel;
    private bool _disposed;
    // UI.7B in-page layer: which one is shown, and whether an accepted key is being written + retested.
    private ServerEditorLayer _layer;
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
        TrustPanel.Configure(localization);
        TrustPanel.AcceptRequested += OnTrustAcceptRequested;
        TrustPanel.CloseRequested += OnTrustCloseRequested;
        ImportPanel.Configure(localization);
        ImportPanel.UseRequested += OnImportUseRequested;
        ImportPanel.CloseRequested += OnImportCloseRequested;
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

    public Task<bool> ConfirmLeaveAsync() => _controller.ConfirmLeaveAsync();

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
        // The controller is gone first, so hiding the layer dismisses / applies nothing.
        HideLayer();
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

            // An editor opened for "Importar de SSH" started its load before the page was shown.
            EvaluateLayer();
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
            or nameof(ServerEditorViewModel.IsConnectionWorkInProgress)
            or nameof(ServerEditorViewModel.IsSshConfigImportOpen)
            or nameof(ServerEditorViewModel.IsLoadingSshConfig)
            or nameof(ServerEditorViewModel.SshConfigHosts)
            or nameof(ServerEditorViewModel.SshConfigLoadOutcome)
            or nameof(ServerEditorViewModel.SshConfigFileWarningMessage))
        {
            EvaluateLayer();
        }
    }

    // The layer host: the controller says which ONE layer the view model needs now. Drawing never accepts or applies.
    private void EvaluateLayer()
    {
        if (_disposed || _viewModel is not { } viewModel)
        {
            return;
        }

        var wanted = _controller.LayerToShow(_acceptingTrust);
        switch (wanted)
        {
            case ServerEditorLayer.Import:
                ImportPanel.Update(viewModel);
                ShowLayer(wanted);
                break;
            case ServerEditorLayer.Trust:
                if (!_acceptingTrust && _controller.PromptToShow() is { } prompt && prompt != TrustPanel.Prompt)
                {
                    TrustPanel.ShowPrompt(prompt);
                }

                TrustPanel.SetWorking(_acceptingTrust, acceptAllowed: !viewModel.IsConnectionWorkInProgress);
                ShowLayer(wanted);
                break;
            default:
                HideLayer();
                break;
        }
    }

    private void ShowLayer(ServerEditorLayer layer)
    {
        var opening = _layer != layer;
        _layer = layer;
        TrustPanel.Visibility = layer == ServerEditorLayer.Trust ? Visibility.Visible : Visibility.Collapsed;
        ImportPanel.Visibility = layer == ServerEditorLayer.Import ? Visibility.Visible : Visibility.Collapsed;
        DialogSurface.MaxWidth = layer == ServerEditorLayer.Trust ? 640 : 720;
        AutomationProperties.SetName(DialogSurface, layer == ServerEditorLayer.Trust ? TrustPanel.Title : ImportPanel.Title);
        DialogLayer.Visibility = Visibility.Visible;
        PageScroll.IsTabStop = false;
        if (opening)
        {
            // Focus moves into the layer (Tab then cycles inside it); the first stop is the safe one.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_layer == ServerEditorLayer.Trust)
                {
                    TrustPanel.FocusSafeButton();
                }
                else if (_layer == ServerEditorLayer.Import)
                {
                    ImportPanel.FocusInitial();
                }
            });
        }
    }

    private void HideLayer()
    {
        if (_layer == ServerEditorLayer.None)
        {
            return;
        }

        _layer = ServerEditorLayer.None;
        DialogLayer.Visibility = Visibility.Collapsed;
        TrustPanel.Visibility = Visibility.Collapsed;
        ImportPanel.Visibility = Visibility.Collapsed;
        if (!_disposed)
        {
            TestButton.Focus(FocusState.Programmatic);
        }
    }

    // "Confiar e testar / continuar": an explicit gesture in the layer, distinct from Save. The layer stays while the key is
    // written and the SAME retest runs, then shows the next prompt (PASSO 2) or closes.
    private async void OnTrustAcceptRequested(object? sender, EventArgs e)
    {
        if (_acceptingTrust || TrustPanel.Prompt is not { CanAccept: true } shown)
        {
            return;
        }

        _acceptingTrust = true;
        TrustPanel.SetWorking(true, acceptAllowed: false);
        try
        {
            await _controller.AcceptTrustAsync(shown);
        }
        finally
        {
            _acceptingTrust = false;
        }

        EvaluateLayer();
    }

    // Cancelar / Voltar ao formulário / Esc: an unknown key is dismissed (nothing written); during an accept the retest is
    // cancelled (a key already being written completes - it was accepted).
    private void OnTrustCloseRequested(object? sender, EventArgs e) => CloseLayer();

    private void OnImportUseRequested(object? sender, EventArgs e)
    {
        // Nothing selected, or a blocked profile: never used, the layer stays.
        if (_controller.ApplyImport(ImportPanel.Selected))
        {
            EvaluateLayer();
        }
    }

    private void OnImportCloseRequested(object? sender, EventArgs e) => CloseLayer();

    private void CloseLayer()
    {
        _controller.CloseLayer(_layer, _acceptingTrust);
        EvaluateLayer();
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
        if (_layer != ServerEditorLayer.None)
        {
            // The layer owns the keys: Esc closes it (a running retest is cancelled first); Enter belongs to the focused
            // button inside it - never the page's Save.
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                CloseLayer();
            }

            return;
        }

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
