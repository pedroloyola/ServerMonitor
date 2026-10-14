using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;
using ServerMonitor.Core.Domain;

namespace ServerMonitor.App.Services;

public sealed class LocalizationService : ILocalizationService
{
    private readonly ILogger<LocalizationService> _logger;
    private readonly IUiLanguagePlatform _platform;
    private readonly Lazy<ResourceLoader> _resourceLoader = new(() => new ResourceLoader());

    public LocalizationService(ILogger<LocalizationService> logger)
        : this(logger, new WindowsUiLanguagePlatform())
    {
    }

    /// <summary>REL.0C test seam: the same service over a fake platform (never the user's real preferences).</summary>
    internal LocalizationService(ILogger<LocalizationService> logger, IUiLanguagePlatform platform)
    {
        _logger = logger;
        _platform = platform;
    }

    public string? CurrentLanguageOverride =>
        string.IsNullOrWhiteSpace(_platform.PrimaryLanguageOverride)
            ? null
            : _platform.PrimaryLanguageOverride;

    public void InitializeFromSystem()
    {
        if (CurrentLanguageOverride is not null)
        {
            _logger.LogInformation("Using the explicit UI language {Language}.", CurrentLanguageOverride);
            return;
        }

        var systemCulture = _platform.SystemUiCultureName;
        if (SupportedCultures.StartupOverride(CurrentLanguageOverride, systemCulture) is { } resolved)
        {
            _platform.PrimaryLanguageOverride = resolved;
            _logger.LogInformation(
                "The Windows UI culture {SystemCulture} is not a supported language; using {ResolvedCulture}.",
                systemCulture,
                resolved);
        }
    }

    public void SetLanguage(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            _platform.PrimaryLanguageOverride = string.Empty;
            InitializeFromSystem();
            return;
        }

        _platform.PrimaryLanguageOverride = SupportedCultures.Resolve(languageTag);
        _logger.LogInformation("UI language changed to {Language}; it will apply after restart.", languageTag);
    }

    public string GetString(string resourceKey)
    {
        try
        {
            return _resourceLoader.Value.GetString(resourceKey);
        }
        catch
        {
            var slashKey = resourceKey.Replace('.', '/');
            return _resourceLoader.Value.GetString(slashKey);
        }
    }

    /// <summary>The real platform: the Windows App SDK override and the current Windows UI culture.</summary>
    private sealed class WindowsUiLanguagePlatform : IUiLanguagePlatform
    {
        public string? PrimaryLanguageOverride
        {
            get => ApplicationLanguages.PrimaryLanguageOverride;
            set => ApplicationLanguages.PrimaryLanguageOverride = value;
        }

        public string SystemUiCultureName => CultureInfo.CurrentUICulture.Name;
    }
}
