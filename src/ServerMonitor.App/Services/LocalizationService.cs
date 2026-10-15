using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.ApplicationModel.Resources;
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
        _formattingCulture = new(() => LanguagePreferencePolicy.AppliedFormattingCulture(
            CurrentLanguageOverride, _platform.SystemUiCultureName, _platform.DisplayCultureName, _settings.Read()));
    }

    public string? CurrentLanguageOverride =>
        string.IsNullOrWhiteSpace(_platform.PrimaryLanguageOverride)
            ? null
            : _platform.PrimaryLanguageOverride;

    public bool FollowsSystemLanguage =>
        LanguagePreferencePolicy.FollowsSystem(CurrentLanguageOverride, _settings.Read());

    public string? PendingLegacyLanguageHint =>
        LanguagePreferencePolicy.LegacyHintTarget(
            CurrentLanguageOverride, _platform.DisplayCultureName, _platform.SystemUiCultureName, _settings.Read());

    /// <summary>
    /// REL.0E: decided once, on first read - after <see cref="InitializeFromSystem"/> in the launch - and kept for the
    /// process, like the text: a language picked in Settings applies after the restart.
    /// </summary>
    public string? FormattingCultureName => _formattingCulture.Value;

    public void InitializeFromSystem()
    {
        // REL.0D D-2, PR #59 round 2 (Vigil L-1): an override that could not be read is NOT "no override" - deciding on it
        // would record System(...) over an explicit choice again (and, on an unsupported Windows language, rewrite it).
        // Like an unreadable record: nothing is written; the next launch decides.
        if (!_platform.IsOverrideKnown)
        {
            _logger.LogWarning("The saved UI language could not be read; the startup language is left unchanged.");
            return;
        }

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
    /// The real platform: the Windows App SDK override and the Windows languages.
    /// REL.0E: the languages are read ONCE, when the platform is built (with the service, before
    /// <see cref="AppFormattingCulture"/> points .NET's UI culture at the app language), so the recovery hint and
    /// "Use system language" keep seeing Windows. .NET caches the display language per process anyway.
    /// F-1 (Cortex A-lite): the "system language" is the profile language of the preferred-language list
    /// (<c>GlobalizationPreferences.Languages</c>, the list MRT reads); the display language stays for the hint condition.
    /// </summary>
    internal sealed class WindowsUiLanguagePlatform : IUiLanguagePlatform
    {
        private readonly string _displayCultureName;
        private readonly string _systemUiCultureName;

        public WindowsUiLanguagePlatform()
            : this(ReadPreferredLanguages)
        {
        }

        /// <summary>Test seam: the preferred-language list from a fake (never the user's real list).</summary>
        internal WindowsUiLanguagePlatform(Func<IReadOnlyList<string>?> preferredLanguages)
        {
            ArgumentNullException.ThrowIfNull(preferredLanguages);
            _displayCultureName = CultureInfo.CurrentUICulture.Name;
            _systemUiCultureName = SupportedCultures.ProfileLanguage(TryRead(preferredLanguages), _displayCultureName);
        }

        /// <summary>
        /// REL.0D D-2: read through <see cref="LanguageOverrideReader"/> - the SDK getter alone is process-local and read
        /// "" on every launch of the installed app. The SDK setter persists it (forwarded to Windows when packaged).
        /// </summary>
        public string? PrimaryLanguageOverride
        {
            get => LanguageOverrideReader.ReadCurrent().Value;
            set => LanguageOverrideReader.WriteCurrent(value); // PR #59 r2 (Cortex M-1): "" clears in Windows, never via the SDK
        }

        /// <summary>PR #59 r2 (Vigil L-1): false when the persisted override or the package identity could not be read.</summary>
        public bool IsOverrideKnown => LanguageOverrideReader.ReadCurrent().IsKnown;

        public string SystemUiCultureName => _systemUiCultureName;

        public string DisplayCultureName => _displayCultureName;

        /// <summary>The user's ordered preferred languages (Settings › Language › Preferred languages).</summary>
        private static IReadOnlyList<string>? ReadPreferredLanguages() =>
            [.. Windows.System.UserProfile.GlobalizationPreferences.Languages];

        /// <summary>
        /// PR #58 round 1 (Cortex L-1, Vigil N-1): this runs in the service's constructor, so in the launch. The list is
        /// optional - the display language is a complete answer, the one every version before F-1 used - so ANY failure
        /// to read it (a WinRT HRESULT mapped to NotSupported/NotImplemented/Argument/…, a type initializer) falls back to
        /// the display language instead of stopping the app.
        /// </summary>
        private static IReadOnlyList<string>? TryRead(Func<IReadOnlyList<string>?> preferredLanguages)
        {
            try
            {
                return preferredLanguages();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
