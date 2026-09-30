using ServerMonitor.App.Services;
using ServerMonitor.Core.Backup;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// State of the "Create encrypted backup" dialog (M14.6). The passphrase lives here only while the
/// dialog is open and is dropped by <see cref="Clear"/>; it is never logged and never part of
/// <see cref="ToString"/>.
/// </summary>
public sealed class BackupCreateSession : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly Func<BackupCreateSession, string, string, Task<bool>> _submit;
    private string _passphrase = string.Empty;
    private string _confirmation = string.Empty;
    private BackupPassphraseProblem _problem = BackupPassphraseProblem.Empty;
    private string _problemMessage = string.Empty;
    private bool _isBusy;

    internal BackupCreateSession(
        ILocalizationService localization,
        Func<BackupCreateSession, string, string, Task<bool>> submit)
    {
        _localization = localization;
        _submit = submit;
        Title = localization.GetString("BackupCreateTitle");
        PassphraseLabel = localization.GetString("BackupPassphraseLabel");
        ConfirmationLabel = localization.GetString("BackupPassphraseConfirmLabel");
        Hint = localization.GetString("BackupPassphraseHint");
        NoRecoveryWarning = localization.GetString("BackupNoRecoveryWarning");
        SecretsNote = localization.GetString("BackupContainsSecretsNote");
        PrimaryText = localization.GetString("BackupCreatePrimary");
        CancelText = localization.GetString("Cancel");
    }

    public string Title { get; }

    public string PassphraseLabel { get; }

    public string ConfirmationLabel { get; }

    public string Hint { get; }

    public string NoRecoveryWarning { get; }

    public string SecretsNote { get; }

    public string PrimaryText { get; }

    public string CancelText { get; }

    /// <summary>Empty while there is nothing worth saying yet (pristine fields).</summary>
    public string ProblemMessage
    {
        get => _problemMessage;
        private set
        {
            if (SetProperty(ref _problemMessage, value))
            {
                OnPropertyChanged(nameof(HasProblem));
            }
        }
    }

    public bool HasProblem => _problemMessage.Length > 0;

    public bool IsBusy
    {
        get => _isBusy;
        internal set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                OnPropertyChanged(nameof(CanSubmit));
            }
        }
    }

    public bool IsNotBusy => !_isBusy;

    public bool CanSubmit => !_isBusy && _problem == BackupPassphraseProblem.None;

    /// <summary>Live validation: called on every change of either field.</summary>
    public void SetPassphrase(string passphrase, string confirmation)
    {
        _passphrase = passphrase ?? string.Empty;
        _confirmation = confirmation ?? string.Empty;
        _problem = BackupPassphrasePolicy.ValidateForExport(_passphrase, _confirmation);

        // An untouched field is not an error yet: the button stays disabled, the message stays quiet.
        var quiet = _problem == BackupPassphraseProblem.Empty
            || (_problem == BackupPassphraseProblem.ConfirmationMismatch && _confirmation.Length == 0);
        ProblemMessage = quiet ? string.Empty : Describe(_problem);
        OnPropertyChanged(nameof(CanSubmit));
    }

    /// <summary><see langword="true"/> when the dialog may close.</summary>
    public async Task<bool> SubmitAsync() =>
        CanSubmit && await _submit(this, _passphrase, _confirmation);

    internal void ShowProblem(BackupPassphraseProblem problem)
    {
        _problem = problem;
        ProblemMessage = Describe(problem);
        OnPropertyChanged(nameof(CanSubmit));
    }

    public void Clear()
    {
        _passphrase = string.Empty;
        _confirmation = string.Empty;
        _problem = BackupPassphraseProblem.Empty;
        ProblemMessage = string.Empty;
        OnPropertyChanged(nameof(CanSubmit));
    }

    public override string ToString() => nameof(BackupCreateSession);

    private string Describe(BackupPassphraseProblem problem) =>
        BackupMessageKeys.ForPassphraseProblem(problem) is { } key ? _localization.GetString(key) : string.Empty;
}

/// <summary>
/// State of the "Restore from backup" passphrase dialog (M14.6). Submitting runs the read-only inspect;
/// the dialog stays open on a wrong passphrase so the user can retry.
/// </summary>
public sealed class RestoreOpenSession : ObservableObject
{
    private readonly Func<RestoreOpenSession, string, Task<bool>> _submit;
    private string _passphrase = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _isBusy;

    internal RestoreOpenSession(
        ILocalizationService localization,
        string fileName,
        Func<RestoreOpenSession, string, Task<bool>> submit)
    {
        _submit = submit;
        Title = localization.GetString("RestoreOpenTitle");
        Message = string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            localization.GetString("RestoreOpenMessage"),
            fileName);
        PassphraseLabel = localization.GetString("BackupPassphraseLabel");
        BusyMessage = localization.GetString("RestoreOpening");
        PrimaryText = localization.GetString("RestoreOpenPrimary");
        CancelText = localization.GetString("Cancel");
    }

    public string Title { get; }

    public string Message { get; }

    public string PassphraseLabel { get; }

    public string BusyMessage { get; }

    public string PrimaryText { get; }

    public string CancelText { get; }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _errorMessage.Length > 0;

    public bool IsBusy
    {
        get => _isBusy;
        internal set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                OnPropertyChanged(nameof(CanSubmit));
            }
        }
    }

    public bool IsNotBusy => !_isBusy;

    /// <summary>False while the inspect runs: one key derivation per click, never two at once.</summary>
    public bool CanSubmit => !_isBusy && _passphrase.Length > 0;

    public void SetPassphrase(string passphrase)
    {
        _passphrase = passphrase ?? string.Empty;
        if (_passphrase.Length > 0)
        {
            ErrorMessage = string.Empty;
        }

        OnPropertyChanged(nameof(CanSubmit));
    }

    /// <summary><see langword="true"/> when the dialog may close.</summary>
    public async Task<bool> SubmitAsync() => CanSubmit && await _submit(this, _passphrase);

    internal void ShowError(string message) => ErrorMessage = message;

    public void Clear()
    {
        _passphrase = string.Empty;
        OnPropertyChanged(nameof(CanSubmit));
    }

    public override string ToString() => nameof(RestoreOpenSession);
}

/// <summary>Everything the destructive restore confirm shows, already localized (contract §5).</summary>
public sealed record RestoreConfirmation(
    string Title,
    string Intro,
    IReadOnlyList<RestoreConfirmationLine> Lines,
    string Note,
    string PrimaryText,
    string CancelText);

public sealed record RestoreConfirmationLine(string Text, bool IsWarning);
