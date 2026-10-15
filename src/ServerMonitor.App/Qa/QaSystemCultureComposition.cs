using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.Globalization;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY (REL.0D). <c>--qa-system-culture &lt;name&gt;</c>: the REAL <see cref="LocalizationService"/> and the real
/// language settings store (re-rooted under the QA root by <see cref="QaStartupIsolation.Apply"/>), over a platform that
/// reports <c>&lt;name&gt;</c> as the Windows UI culture. The language override itself is the real one (it does not
/// persist on the unpackaged Debug build). The user's Windows language is never read or changed.
/// Excluded from Release (see ServerMonitor.App.csproj).
/// </summary>
internal static class QaSystemCultureComposition
{
    public static string? RequestedCulture() =>
        QaSystemCulturePolicy.ResolveCulture(Environment.GetCommandLineArgs(), isDebugBuild: true);

    /// <summary>Registered last, so it replaces the production <see cref="ILocalizationService"/>.</summary>
    public static void Apply(IServiceCollection services, string systemCulture)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemCulture);
        services.AddSingleton<ILocalizationService>(sp => new LocalizationService(
            sp.GetRequiredService<ILogger<LocalizationService>>(),
            new ForcedCulturePlatform(systemCulture),
            sp.GetRequiredService<ILanguageSettingsStore>()));
    }

    private sealed class ForcedCulturePlatform(string systemCulture) : IUiLanguagePlatform
    {
        public string? PrimaryLanguageOverride
        {
            get => LanguageOverrideReader.ReadCurrent(); // REL.0D D-2: the same read as the real platform
            set => ApplicationLanguages.PrimaryLanguageOverride = value;
        }

        public string SystemUiCultureName => systemCulture;
    }
}
