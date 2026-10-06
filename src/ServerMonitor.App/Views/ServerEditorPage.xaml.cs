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
/// </summary>
public sealed partial class ServerEditorPage : Page, IServerEditorView, INavigationExitGuard, IDisposable
{
    private readonly ServerEditorPageController _controller;
    private readonly ILocalizationService _localization;
    private ServerEditorViewModel? _viewModel;
    private bool _disposed;

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
    }

    private void UpdateActionState()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var busy = viewModel.IsConnectionWorkInProgress;
        TestButton.IsEnabled = !busy;
        PrimaryButton.IsEnabled = !busy;
        HeaderButton.IsEnabled = !busy || _controller.IsEdit;
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
        else if (_viewModel is { } viewModel)
        {
            await viewModel.LoadSshConfigHostsAsync();
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
