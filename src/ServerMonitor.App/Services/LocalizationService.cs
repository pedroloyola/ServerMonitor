using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;
using ServerMonitor.Core.Domain;

namespace ServerMonitor.App.Services;

/// <summary>
/// The UI language: the Windows App SDK override (<see cref="IUiLanguagePlatform"/>) plus, since REL.0D, its provenance
/// (<see cref="ILanguageSettingsStore"/>). The decisions are the pure <see cref="LanguagePreferencePolicy"/>; this class
/// only reads, applies them in order (record first, then the override) and never writes the override when its record
/// could not be saved.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    private readonly ILogger<LocalizationService> _logger;
    private readonly IUiLanguagePlatform _platform;
    private readonly ILanguageSettingsStore _settings;
    private readonly Lazy<ResourceLoader> _resourceLoader = new(() => new ResourceLoader());
    private readonly Lazy<string?> _formattingCulture;

    public LocalizationService(ILogger<LocalizationService> logger, ILanguageSettingsStore settings)
        : this(logger, new WindowsUiLanguagePlatform(), settings)
    {
    }

    /// <summary>REL.0C test seam: the same service over a fake platform (never the user's real preferences).</summary>
    internal LocalizationService(ILogger<LocalizationService> logger, IUiLanguagePlatform platform, ILanguageSettingsStore settings)
    {
        _logger = logger;
        _platform = platform;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _formattingCulture = new(() =>
            LanguagePreferencePolicy.FormattingCulture(CurrentLanguageOverride, _platform.SystemUiCultureName, _settings.Read()));
    }

    public string? CurrentLanguageOverride =>
        string.IsNullOrWhiteSpace(_platform.PrimaryLanguageOverride)
            ? null
            : _platform.PrimaryLanguageOverride;

    public bool FollowsSystemLanguage =>
        LanguagePreferencePolicy.FollowsSystem(CurrentLanguageOverride, _settings.Read());

    public string? PendingLegacyLanguageHint =>
        LanguagePreferencePolicy.LegacyHintTarget(CurrentLanguageOverride, _platform.SystemUiCultureName, _settings.Read());

    /// <summary>
    /// REL.0E: decided once, on first read - after <see cref="InitializeFromSystem"/> in the launch - and kept for the
    /// process, like the text: a language picked in Settings applies after the restart.
    /// </summary>
    public string? FormattingCultureName => _formattingCulture.Value;

    public void InitializeFromSystem()
    {
        var current = CurrentLanguageOverride;
        var systemCulture = _platform.SystemUiCultureName;
        var plan = LanguagePreferencePolicy.AtStartup(current, systemCulture, _settings.Read());
        if (plan.IsEmpty)
        {
            if (current is not null)
            {
                _logger.LogInformation("Using the UI language {Language}.", current);
            }

            return;
        }

        // REL.0D: the record goes first. If it cannot be saved the override is not written either, so nothing the app
        // writes can later be mistaken for the user's choice.
        if (plan.RecordToWrite is { } record && !_settings.TryWrite(record))
        {
            return;
        }

        if (plan.OverrideToWrite is { } language)
        {
            _platform.PrimaryLanguageOverride = language;
            _logger.LogInformation(
                "Following the Windows UI culture {SystemCulture}: using {ResolvedCulture}.",
                systemCulture,
                language.Length == 0 ? "the system language" : language);
        }
    }

    public void SetLanguage(string? languageTag) => TrySetLanguage(languageTag);

    public bool TrySetLanguage(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            // REL.0D (Cortex N-1): "Use system language" is recorded, so it persists and keeps following Windows.
            var target = LanguagePreferencePolicy.SystemOverride(_platform.SystemUiCultureName);
            if (!_settings.TryWrite(LanguageProvenance.System(target)))
            {
                return false;
            }

            _platform.PrimaryLanguageOverride = target;
            _logger.LogInformation("UI language set to follow the system; it will apply after restart.");
            return true;
        }

        var resolved = SupportedCultures.Resolve(languageTag);
        if (!_settings.TryWrite(LanguageProvenance.Explicit))
        {
            return false;
        }

        _platform.PrimaryLanguageOverride = resolved;
        _logger.LogInformation("UI language changed to {Language}; it will apply after restart.", resolved);
        return true;
    }

    public bool KeepCurrentLanguage()
    {
        if (!_settings.TryWrite(LanguageProvenance.Explicit))
        {
            return false;
        }

        _logger.LogInformation("The UI language {Language} was kept as the user's choice.", CurrentLanguageOverride);
        return true;
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

    /// <summary>
    /// The real platform: the Windows App SDK override and the Windows UI culture.
    /// REL.0E: the culture is read ONCE, when the platform is built (with the service, before
    /// <see cref="AppFormattingCulture"/> points .NET's UI culture at the app language), so the recovery hint and
    /// "Use system language" keep seeing Windows. .NET caches it per process anyway: nothing more recent was ever seen.
    /// </summary>
    internal sealed class WindowsUiLanguagePlatform : IUiLanguagePlatform
    {
        private readonly string _systemUiCultureName = CultureInfo.CurrentUICulture.Name;

        /// <summary>
        /// REL.0D D-2: read through <see cref="LanguageOverrideReader"/> - the SDK getter alone is process-local and read
        /// "" on every launch of the installed app. The SDK setter persists it (forwarded to Windows when packaged).
        /// </summary>
        public string? PrimaryLanguageOverride
        {
            get => LanguageOverrideReader.ReadCurrent();
            set => ApplicationLanguages.PrimaryLanguageOverride = value;
        }

        public string SystemUiCultureName => _systemUiCultureName;
    }
}
