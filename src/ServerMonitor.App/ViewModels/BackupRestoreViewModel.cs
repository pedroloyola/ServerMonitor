using System.Globalization;
using System.IO;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Backup;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// The Settings "Backup and restore" section (M14.6): encrypted export and the destructive REPLACE
/// restore. It owns the <see cref="RestorePlan"/> between inspect and apply and disposes it on every
/// exit path. Passphrases are handed straight to the service and never logged; failures are logged by
/// code and exception type only.
/// </summary>
public sealed class BackupRestoreViewModel : ObservableObject, IDisposable
{
    public const string BackupFileExtension = ".serveralyzer-backup";

    private readonly IConfigurationBackupService _backupService;
    private readonly IBackupFilePicker _filePicker;
    private readonly IBackupRestoreInteraction _interaction;
    private readonly IAppLifecycleController _lifecycle;
    private readonly ILocalizationService _localization;
    private readonly ILogger<BackupRestoreViewModel> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly RelayCommand _createBackupCommand;
    private readonly RelayCommand _restoreCommand;
    private RestorePlan? _plan;
    private bool _isRunning;
    private bool _isApplying;
    private bool _isBlocked;
    private bool _isRestoreCommitted;
    private bool _disposed;
    private int _startupRecoveryShown;
    private bool _isStatusOpen;
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private string _statusTitle = string.Empty;
    private string _statusMessage = string.Empty;
    private string _statusDetail = string.Empty;

    public BackupRestoreViewModel(
        IConfigurationBackupService backupService,
        IBackupFilePicker filePicker,
        IBackupRestoreInteraction interaction,
        IAppLifecycleController lifecycle,
        ILocalizationService localization,
        ILogger<BackupRestoreViewModel> logger,
        TimeProvider? timeProvider = null)
    {
        _backupService = backupService;
        _filePicker = filePicker;
        _interaction = interaction;
        _lifecycle = lifecycle;
        _localization = localization;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        // The buttons stay enabled while a flow runs: its dialogs are modal, re-entry is refused by
        // TryBegin, and a button that disables itself under the keyboard focus sends the focus (and the
        // scroll position) to the top of the page.
        _createBackupCommand = new RelayCommand(() => _ = CreateBackupAsync(), () => IsAvailable);
        _restoreCommand = new RelayCommand(() => _ = RestoreAsync(), () => IsAvailable);
    }

    public ICommand CreateBackupCommand => _createBackupCommand;

    public ICommand RestoreCommand => _restoreCommand;

    /// <summary>False while a stuck journal blocks the feature, and once a restore committed (the app is
    /// about to close and configuration writes are refused).</summary>
    public bool IsAvailable => !_isBlocked && !_isRestoreCommitted;

    /// <summary>False while a flow is already running, or the feature is not available.</summary>
    public bool CanStart => !_isRunning && IsAvailable;

    public bool IsBlocked => _isBlocked;

    public bool IsStatusOpen
    {
        get => _isStatusOpen;
        set => SetProperty(ref _isStatusOpen, value);
    }

    /// <summary>The blocked state is not an event: it stays until the app restarts.</summary>
    public bool IsStatusClosable => !_isBlocked;

    public InfoBarSeverity StatusSeverity
    {
        get => _statusSeverity;
        private set => SetProperty(ref _statusSeverity, value);
    }

    public string StatusTitle
    {
        get => _statusTitle;
        private set => SetProperty(ref _statusTitle, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Selectable detail under the message: the stuck-journal folder (N4).</summary>
    public string StatusDetail
    {
        get => _statusDetail;
        private set
        {
            if (SetProperty(ref _statusDetail, value))
            {
                OnPropertyChanged(nameof(HasStatusDetail));
            }
        }
    }

    public bool HasStatusDetail => _statusDetail.Length > 0;

    /// <summary>
    /// Surfaces what startup recovery did with an interrupted restore, exactly once per process. Called
    /// when the window is first ready, so a headless start shows it when the Dashboard materializes.
    /// </summary>
    public async Task ShowStartupRecoveryOnceAsync()
    {
        if (Interlocked.Exchange(ref _startupRecoveryShown, 1) == 1)
        {
            return;
        }

        try
        {
            var report = _backupService.StartupRecovery;
            if (report is null || BackupMessageKeys.ForStartupRecovery(report.Outcome) is not { } key)
            {
                return;
            }

            string message;
            if (report.Outcome == RestoreRecoveryOutcome.Stuck)
            {
                ShowBlocked(report.JournalDirectory);
                message = StatusMessage;
            }
            else
            {
                message = GetString(key);
            }

            await _interaction.ShowNoticeAsync(
                GetString("AppWindowTitle"),
                message,
                GetString("ServerFormSshConfigCloseButton.Content"));
        }
        catch (Exception exception)
        {
            LogFailure("show the startup recovery outcome", exception);
        }
    }

    public async Task CreateBackupAsync()
    {
        if (!TryBegin())
        {
            return;
        }

        try
        {
            IsStatusOpen = false;
            var session = new BackupCreateSession(_localization, SubmitCreateAsync);
            try
            {
                await _interaction.ShowCreateDialogAsync(session);
            }
            finally
            {
                session.Clear();
            }
        }
        catch (Exception exception)
        {
            LogFailure("create a backup", exception);
            ShowStatus(InfoBarSeverity.Error, GetString("BackupFailedTitle"), GetString(BackupMessageKeys.Generic));
        }
        finally
        {
            End();
        }
    }

    public async Task RestoreAsync()
    {
        if (!TryBegin())
        {
            return;
        }

        var applyStarted = false;
        try
        {
            IsStatusOpen = false;
            var path = await _filePicker.PickOpenAsync();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var session = new RestoreOpenSession(
                _localization,
                Path.GetFileName(path),
                (openSession, passphrase) => InspectAsync(path, openSession, passphrase));
            try
            {
                await _interaction.ShowRestoreOpenDialogAsync(session);
            }
            finally
            {
                session.Clear();
            }

            var plan = _plan;
            if (plan is null)
            {
                return;
            }

            // The destructive step needs the explicit confirm; anything else leaves everything as it was.
            if (!await _interaction.ConfirmRestoreAsync(BuildConfirmation(plan.Summary)))
            {
                return;
            }

            if (_disposed || plan.IsDisposed)
            {
                return;
            }

            RestoreApplyResult result;
            applyStarted = true;
            _isApplying = true;
            try
            {
                result = await _interaction.ShowRestoringAsync(
                    GetString("RestoreApplying"),
                    () => _backupService.ApplyAsync(plan));
            }
            finally
            {
                _isApplying = false;
                ReleasePlan();
            }

            await HandleApplyResultAsync(result);
        }
        catch (Exception exception)
        {
            LogFailure("restore a backup", exception);
            // Once apply has started nothing may be claimed about the configuration.
            ShowStatus(
                InfoBarSeverity.Error,
                GetString("RestoreRolledBackTitle"),
                GetString(applyStarted ? "ServerOperationError.Title" : BackupMessageKeys.Generic));
        }
        finally
        {
            ReleasePlan();
            End();
        }
    }

    /// <summary>Host shutdown / window teardown: a plan still waiting for its confirm is zeroed here.</summary>
    public void Dispose()
    {
        _disposed = true;
        if (!_isApplying)
        {
            ReleasePlan();
        }
    }

    internal RestoreConfirmation BuildConfirmation(RestoreSummary summary)
    {
        var lines = new List<RestoreConfirmationLine>();
        var backup = summary.Backup;
        var current = summary.Current;

        lines.Add(new(Format(
            "RestoreSummaryServers",
            current.DirectServers + current.RoutedServers,
            backup.DirectServers + backup.RoutedServers,
            backup.RoutedServers), false));

        var backupTrust = Sum(backup.DirectTrustedHostKeys, backup.RoutedTrustedHostKeys);
        lines.Add(new(Format(
            "RestoreSummaryTrust",
            CountOrUnknown(Sum(current.DirectTrustedHostKeys, current.RoutedTrustedHostKeys)),
            CountOrUnknown(backupTrust)), false));

        var toRemove = Sum(summary.DirectTrustedHostKeysToRemove, summary.RoutedTrustedHostKeysToRemove);
        if (toRemove is null or > 0)
        {
            lines.Add(new(Format(
                "RestoreSummaryTrustRemoved",
                toRemove?.ToString(CultureInfo.CurrentUICulture) ?? GetString("RestoreSummarySome")), true));
        }

        if (backupTrust is null or > 0)
        {
            lines.Add(new(GetString("RestoreSummaryTrustDelegated"), true));
        }

        lines.Add(new(Format("RestoreSummaryPasswords", backup.Credentials), false));

        if (summary.MissingCredentials.Count > 0)
        {
            lines.Add(new(Format("RestoreSummaryMissingPasswords", Names(summary.MissingCredentials)), true));
        }

        foreach (var status in new[] { KeyPathStatus.Missing, KeyPathStatus.NotChecked, KeyPathStatus.Unsupported })
        {
            var names = summary.KeyPathWarnings
                .Where(warning => warning.Status == status)
                .Select(warning => DisplayName(warning.ServerName, warning.IsJump))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (names.Count > 0)
            {
                lines.Add(new(Format(BackupMessageKeys.ForKeyPathStatus(status), string.Join(", ", names)), true));
            }
        }

        lines.Add(new(GetString(summary.BackupSettings == summary.CurrentSettings
            ? "RestoreSummaryHistoryKept"
            : "RestoreSummarySettings"), false));

        return new RestoreConfirmation(
            GetString("RestoreConfirmTitle"),
            Format("RestoreConfirmIntro", summary.BackupCreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture)),
            lines,
            GetString("RestoreConfirmNote"),
            GetString("RestoreConfirmPrimary"),
            GetString("Cancel"));
    }

    private async Task<bool> SubmitCreateAsync(BackupCreateSession session, string passphrase, string confirmation)
    {
        session.IsBusy = true;
        try
        {
            var suggestedName = string.Format(
                CultureInfo.InvariantCulture,
                "ServerAlyzer-backup-{0:yyyy-MM-dd}{1}",
                _timeProvider.GetLocalNow(),
                BackupFileExtension);
            var path = await _filePicker.PickSaveAsync(suggestedName, GetString("BackupFileTypeLabel"));
            if (string.IsNullOrEmpty(path))
            {
                return false; // picker canceled: back to the dialog, nothing written
            }

            var result = await _backupService.ExportAsync(path, passphrase.AsMemory(), confirmation.AsMemory());
            if (result.PassphraseProblem != BackupPassphraseProblem.None)
            {
                session.ShowProblem(result.PassphraseProblem);
                return false;
            }

            if (result.Succeeded)
            {
                ShowExportSuccess(result.Summary ?? new BackupExportSummary(), path);
                return true;
            }

            var error = result.Error!.Value;
            if (error == BackupError.RestorePending)
            {
                ShowBlocked(result.PendingJournalDirectory);
                return true;
            }

            if (BackupMessageKeys.ForExportError(error) is not { } key)
            {
                return false; // canceled: silent
            }

            _logger.LogWarning("Backup export failed. Code: {BackupError}.", error);
            ShowStatus(InfoBarSeverity.Error, GetString("BackupFailedTitle"), GetString(key));
            return true;
        }
        catch (Exception exception)
        {
            LogFailure("create a backup", exception);
            ShowStatus(InfoBarSeverity.Error, GetString("BackupFailedTitle"), GetString(BackupMessageKeys.Generic));
            return true;
        }
        finally
        {
            session.IsBusy = false;
        }
    }

    private async Task<bool> InspectAsync(string path, RestoreOpenSession session, string passphrase)
    {
        var problem = BackupPassphrasePolicy.ValidateForRestore(passphrase);
        if (problem != BackupPassphraseProblem.None)
        {
            session.ShowError(GetString(BackupMessageKeys.ForPassphraseProblem(problem) ?? BackupMessageKeys.Generic));
            return false;
        }

        session.IsBusy = true;
        try
        {
            var result = await _backupService.InspectAsync(path, passphrase.AsMemory());
            if (result.Plan is { } plan)
            {
                ReleasePlan();
                if (_disposed)
                {
                    plan.Dispose();
                }
                else
                {
                    _plan = plan;
                }

                return true;
            }

            if (result.PassphraseProblem != BackupPassphraseProblem.None)
            {
                session.ShowError(GetString(BackupMessageKeys.ForPassphraseProblem(result.PassphraseProblem) ?? BackupMessageKeys.Generic));
                return false;
            }

            var error = result.Error;
            switch (error)
            {
                case BackupError.WrongPassphraseOrDamaged:
                    // Stays in the dialog: the likely cause is a typo, and there is no retry limit.
                    session.ShowError(GetString(BackupMessageKeys.ForInspectError(error.Value) ?? BackupMessageKeys.Generic));
                    return false;
                case BackupError.Canceled:
                    return false;
                case BackupError.RestorePending:
                    ShowBlocked(result.PendingJournalDirectory);
                    return true;
                default:
                    _logger.LogWarning("Backup inspect failed. Code: {BackupError}.", error);
                    ShowStatus(
                        InfoBarSeverity.Error,
                        GetString("RestoreRolledBackTitle"),
                        GetString(error is { } code
                            ? BackupMessageKeys.ForInspectError(code) ?? BackupMessageKeys.Generic
                            : BackupMessageKeys.Generic));
                    return true;
            }
        }
        catch (Exception exception)
        {
            // Inspect touches nothing, so "nothing was lost" holds whatever went wrong.
            LogFailure("open a backup", exception);
            ShowStatus(
                InfoBarSeverity.Error,
                GetString("RestoreRolledBackTitle"),
                GetString(BackupMessageKeys.Generic));
            return true;
        }
        finally
        {
            session.IsBusy = false;
        }
    }

    private async Task HandleApplyResultAsync(RestoreApplyResult result)
    {
        var message = GetString(BackupMessageKeys.ForApplyOutcome(result.Outcome));
        switch (result.Outcome)
        {
            case RestoreApplyOutcome.Completed:
                // Configuration writes are refused from here until the process ends: offer nothing else.
                _isRestoreCommitted = true;
                NotifyCanStartChanged();
                try
                {
                    await _interaction.ShowRestoreCompletedAsync(
                        GetString("RestoreCompletedTitle"),
                        message,
                        GetString("RestoreCompletedPrimary"));
                }
                catch (Exception exception)
                {
                    LogFailure("show the restore completion", exception);
                }
                finally
                {
                    _lifecycle.RequestExit(ExitReason.RestoreCompleted);
                }

                break;
            case RestoreApplyOutcome.Canceled:
                ShowStatus(InfoBarSeverity.Informational, string.Empty, message);
                break;
            case RestoreApplyOutcome.Busy:
                ShowStatus(InfoBarSeverity.Warning, string.Empty, message);
                break;
            case RestoreApplyOutcome.RestorePending:
                ShowBlocked(result.JournalDirectory);
                break;
            default:
                _logger.LogWarning(
                    "Restore did not complete. Outcome: {Outcome}. Cause: {BackupError}.",
                    result.Outcome,
                    result.Error);
                ShowStatus(InfoBarSeverity.Error, GetString("RestoreRolledBackTitle"), message);
                break;
        }
    }

    private void ShowExportSuccess(BackupExportSummary summary, string path)
    {
        var lines = new List<string>
        {
            Format(
                "BackupCreatedMessage",
                summary.DirectServers + summary.RoutedServers,
                summary.DirectTrustedHostKeys + summary.RoutedTrustedHostKeys,
                summary.Credentials,
                path)
        };

        var incomplete = false;
        if (summary.MissingCredentials.Count > 0)
        {
            incomplete = true;
            lines.Add(Format("BackupMissingCredentialsWarning", Names(summary.MissingCredentials)));
        }

        if (summary.ExcludedUnreadableServers > 0)
        {
            incomplete = true;
            lines.Add(Format("BackupExcludedEntriesWarning", summary.ExcludedUnreadableServers));
        }

        if (summary.ExcludedUnreferencedTrustedHostKeys > 0)
        {
            lines.Add(Format("BackupExcludedTrustNote", summary.ExcludedUnreferencedTrustedHostKeys));
        }

        // UI.10 F29: the title says what the severity says - with entries left out it is "criada com avisos", not "criada".
        ShowStatus(
            incomplete ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
            GetString(incomplete ? "BackupCreatedWithWarningsTitle" : "BackupCreatedTitle"),
            string.Join(Environment.NewLine, lines));
    }

    private void ShowBlocked(string? journalDirectory)
    {
        var directory = string.IsNullOrEmpty(journalDirectory)
            ? _backupService.StartupRecovery?.JournalDirectory ?? string.Empty
            : journalDirectory;
        if (!_isBlocked)
        {
            _isBlocked = true;
            OnPropertyChanged(nameof(IsBlocked));
            OnPropertyChanged(nameof(IsStatusClosable));
            NotifyCanStartChanged();
        }

        ShowStatus(InfoBarSeverity.Warning, string.Empty, Format(BackupMessageKeys.Blocked, directory), directory);
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message, string detail = "")
    {
        StatusSeverity = severity;
        StatusTitle = title;
        StatusMessage = message;
        StatusDetail = detail;
        IsStatusOpen = true;
    }

    private bool TryBegin()
    {
        if (!CanStart)
        {
            return false;
        }

        _isRunning = true;
        NotifyCanStartChanged();
        return true;
    }

    private void End()
    {
        _isRunning = false;
        NotifyCanStartChanged();
    }

    private void NotifyCanStartChanged()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(IsAvailable));
        _createBackupCommand.NotifyCanExecuteChanged();
        _restoreCommand.NotifyCanExecuteChanged();
    }

    private void ReleasePlan() => Interlocked.Exchange(ref _plan, null)?.Dispose();

    private void LogFailure(string operation, Exception exception) =>
        _logger.LogError(
            "Could not {Operation}. Exception type: {ExceptionType}.",
            operation,
            exception.GetType().Name);

    private string GetString(string key) => _localization.GetString(key);

    private string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentUICulture, GetString(key), arguments);

    private string CountOrUnknown(int? count) =>
        count?.ToString(CultureInfo.CurrentUICulture) ?? GetString("RestoreSummaryUnknown");

    private static int? Sum(int? first, int? second) => first is null || second is null ? null : first + second;

    private static string Names(IEnumerable<BackupCredentialFlag> flags) => string.Join(
        ", ",
        flags.Select(flag => DisplayName(flag.ServerName, flag.IsJump)).Distinct(StringComparer.Ordinal));

    private static string DisplayName(string serverName, bool isJump) =>
        isJump ? serverName + " (jump host)" : serverName;
}
