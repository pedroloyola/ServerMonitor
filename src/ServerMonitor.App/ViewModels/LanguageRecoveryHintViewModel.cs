using System.Globalization;
using System.Windows.Input;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Domain;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// REL.0D (human GO D-B1 + D-B2): the one-time, non-blocking language hint on Overview. It appears only when the
/// real <see cref="ILocalizationService"/> reports the exact signature of the v1.x defect (a pt-BR override with no
/// provenance record on a Windows language that is not exactly en-US / pt-PT / pt-BR). It never changes the language
/// on its own:
/// <list type="bullet">
/// <item>"Use the system language" = exactly Settings › "Use system language" (<c>TrySetLanguage(null)</c>), then the
/// usual restart notice.</item>
/// <item>"Keep Português (Brasil)" records pt-BR as the user's own choice.</item>
/// </list>
/// Either decision writes the provenance record, so the hint never comes back; the language can still be changed in
/// Settings. When the decision cannot be saved, nothing changes, the hint stays and an error says so.
/// </summary>
public sealed class LanguageRecoveryHintViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly string? _target;
    private bool _isOpen;
    private bool _isRestartNoticeOpen;
    private bool _isSaveErrorOpen;

    public LanguageRecoveryHintViewModel(ILocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _target = localization.PendingLegacyLanguageHint;
        _isOpen = _target is not null;
        UseSystemLanguageCommand = new RelayCommand(UseSystemLanguage);
        KeepCurrentLanguageCommand = new RelayCommand(KeepCurrentLanguage);
    }

    /// <summary>The hint itself (open until the user decides; never re-opened in this process).</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (SetProperty(ref _isOpen, value))
            {
                OnPropertyChanged(nameof(HasVisibleNotice));
            }
        }
    }

    /// <summary>
    /// The area's visibility: collapsed when none of its three notices is open, so it adds no gap to the page and its
    /// Rise entrance plays when one appears.
    /// </summary>
    public bool HasVisibleNotice => _isOpen || _isRestartNoticeOpen || _isSaveErrorOpen;

    /// <summary>After "Use the system language": the restart notice (closable).</summary>
    public bool IsRestartNoticeOpen
    {
        get => _isRestartNoticeOpen;
        set
        {
            if (SetProperty(ref _isRestartNoticeOpen, value))
            {
                OnPropertyChanged(nameof(HasVisibleNotice));
            }
        }
    }

    /// <summary>The decision could not be saved: nothing changed, the hint is still there.</summary>
    public bool IsSaveErrorOpen
    {
        get => _isSaveErrorOpen;
        set
        {
            if (SetProperty(ref _isSaveErrorOpen, value))
            {
                OnPropertyChanged(nameof(HasVisibleNotice));
            }
        }
    }

    /// <summary>The language "Use the system language" leads to (en-US or pt-PT), or null when no hint is due.</summary>
    public string? TargetLanguage => _target;

    public string Title => _localization.GetString("LanguageRecoveryHintTitle");

    /// <summary>States the effective language when the Windows language itself is not available (fr-FR → English).</summary>
    public string Message => _target is null
        ? string.Empty
        : string.Format(
            CultureInfo.CurrentCulture,
            _localization.GetString("LanguageRecoveryHintMessageFormat"),
            LanguageName(_localization, _target));

    public ICommand UseSystemLanguageCommand { get; }

    public ICommand KeepCurrentLanguageCommand { get; }

    /// <summary>The picker's own name for a supported language (one wording for the same language everywhere).</summary>
    internal static string LanguageName(ILocalizationService localization, string language) =>
        localization.GetString(language switch
        {
            SupportedCultures.EuropeanPortuguese => "LanguageEuropeanPortugueseOption.Content",
            SupportedCultures.BrazilianPortuguese => "LanguageBrazilianPortugueseOption.Content",
            _ => "LanguageEnglishOption.Content"
        });

    /// <summary>
    /// PR #55 fix round 1 (Relay L-1 / Cortex N-3): this view model is a singleton, and the language can be decided
    /// elsewhere in the same session (Settings › Language). Each Overview visit re-checks the real state: when no hint is
    /// due any more, the hint closes - without writing anything and without a restart notice. Never re-opens one.
    /// </summary>
    public void Refresh()
    {
        if (IsOpen && _localization.PendingLegacyLanguageHint is null)
        {
            IsSaveErrorOpen = false;
            IsOpen = false;
        }
    }

    /// <summary>An action on a hint that is no longer due only closes it (the user's later decision stands).</summary>
    private bool StillDue()
    {
        if (!IsOpen)
        {
            return false;
        }

        Refresh();
        return IsOpen;
    }

    private void UseSystemLanguage()
    {
        if (!StillDue())
        {
            return;
        }

        if (!_localization.TrySetLanguage(null))
        {
            IsSaveErrorOpen = true;
            return;
        }

        IsSaveErrorOpen = false;
        IsOpen = false;
        IsRestartNoticeOpen = true;
    }

    private void KeepCurrentLanguage()
    {
        if (!StillDue())
        {
            return;
        }

        if (!_localization.KeepCurrentLanguage())
        {
            IsSaveErrorOpen = true;
            return;
        }

        IsSaveErrorOpen = false;
        IsOpen = false;
    }
}
