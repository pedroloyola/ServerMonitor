using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;
using ServerMonitor.Core.Domain;

namespace ServerMonitor.App.Services;

public sealed class LocalizationService(ILogger<LocalizationService> logger) : ILocalizationService
{
    private readonly Lazy<ResourceLoader> _resourceLoader = new(() => new ResourceLoader());

    public string? CurrentLanguageOverride =>
        string.IsNullOrWhiteSpace(ApplicationLanguages.PrimaryLanguageOverride)
            ? null
            : ApplicationLanguages.PrimaryLanguageOverride;

    public void InitializeFromSystem()
    {
        if (CurrentLanguageOverride is not null)
        {
            logger.LogInformation("Using the explicit UI language {Language}.", CurrentLanguageOverride);
            return;
        }

        var systemCulture = CultureInfo.CurrentUICulture.Name;
        if (!SupportedCultures.IsSupported(systemCulture))
        {
            ApplicationLanguages.PrimaryLanguageOverride = SupportedCultures.Default;
            logger.LogInformation(
                "The Windows UI culture {SystemCulture} is unsupported; using {FallbackCulture}.",
                systemCulture,
                SupportedCultures.Default);
        }
    }

    public void SetLanguage(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            ApplicationLanguages.PrimaryLanguageOverride = string.Empty;
            InitializeFromSystem();
            return;
        }

        ApplicationLanguages.PrimaryLanguageOverride = SupportedCultures.Resolve(languageTag);
        logger.LogInformation("UI language changed to {Language}; it will apply after restart.", languageTag);
    }

    public string GetString(string resourceKey) => _resourceLoader.Value.GetString(resourceKey);
}
