using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly ILocalizationService _localizationService;
    private readonly IThemeService _themeService;
    private int _selectedLanguageIndex;
    private int _selectedThemeIndex;
    private bool _isRestartNoticeOpen;

    public SettingsViewModel(IThemeService themeService, ILocalizationService localizationService)
    {
        _themeService = themeService;
        _localizationService = localizationService;
        _selectedThemeIndex = (int)themeService.Current;
        _selectedLanguageIndex = localizationService.CurrentLanguageOverride switch
        {
            "pt-BR" => 1,
            "pt-PT" => 2,
            "en-US" => 3,
            _ => 0
        };
    }

    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set
        {
            if (SetProperty(ref _selectedThemeIndex, value) && Enum.IsDefined((AppThemePreference)value))
            {
                _themeService.Apply((AppThemePreference)value);
            }
        }
    }

    public int SelectedLanguageIndex
    {
        get => _selectedLanguageIndex;
        set
        {
            if (!SetProperty(ref _selectedLanguageIndex, value))
            {
                return;
            }

            var languageTag = value switch
            {
                1 => "pt-BR",
                2 => "pt-PT",
                3 => "en-US",
                _ => null
            };

            _localizationService.SetLanguage(languageTag);
            IsRestartNoticeOpen = true;
        }
    }

    public bool IsRestartNoticeOpen
    {
        get => _isRestartNoticeOpen;
        set => SetProperty(ref _isRestartNoticeOpen, value);
    }
}
