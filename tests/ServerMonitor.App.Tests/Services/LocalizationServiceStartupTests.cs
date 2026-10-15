using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Tests.TestSupport;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// REL.0C (Atlas PR46-01): the REAL <see cref="LocalizationService"/> startup resolution, over a fake
/// <see cref="IUiLanguagePlatform"/> (never the user's Windows language preferences), and the launch path that applies
/// it: <see cref="App.BuildServicesHost"/> hands out the host only after InitializeFromSystem ran.
/// </summary>
public sealed class LocalizationServiceStartupTests
{
    [Theory]
    [InlineData("en-GB", "en-US")]
    [InlineData("fr-FR", "en-US")]
    [InlineData("de", "en-US")]
    [InlineData("", "en-US")]
    [InlineData("pt-AO", "pt-PT")]
    public void FirstRun_OnAnUnsupportedCulture_WritesExactlyTheResolvedLanguage(string systemCulture, string expected)
    {
        var platform = new FakeUiLanguagePlatform(systemCulture);

        Service(platform).InitializeFromSystem();

        Assert.Equal([expected], platform.Writes);
        Assert.Equal(expected, Service(platform).CurrentLanguageOverride);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void FirstRun_OnAnExactlySupportedCulture_WritesNothing(string systemCulture)
    {
        var platform = new FakeUiLanguagePlatform(systemCulture);
        var service = Service(platform);

        service.InitializeFromSystem();

        Assert.Empty(platform.Writes);
        Assert.Null(service.CurrentLanguageOverride);
    }

    [Theory]
    [InlineData("pt-BR", "fr-FR")] // the old pt-BR default persisted on an unsupported system: never migrated
    [InlineData("pt-BR", "en-GB")]
    [InlineData("pt-PT", "en-US")]
    [InlineData("en-US", "pt-AO")]
    public void APersistedChoice_IsNeverOverwritten(string persisted, string systemCulture)
    {
        var platform = new FakeUiLanguagePlatform(systemCulture, persisted);
        var service = Service(platform);

        service.InitializeFromSystem();

        Assert.Empty(platform.Writes);
        Assert.Equal(persisted, service.CurrentLanguageOverride);
    }

    /// <summary>REL.0D: one write - the system language's own override - after its provenance record.</summary>
    [Fact]
    public void ChoosingTheSystemLanguage_WritesTheSystemCulturesResolution()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR", "pt-BR");

        Service(platform).SetLanguage(null);

        Assert.Equal(["en-US"], platform.Writes);
    }

    [Fact]
    public void TheLaunchHost_IsReturnedOnlyAfterTheRealServiceAppliedTheStartupLanguage()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR");

        using var host = App.BuildServicesHost(
            services => services.AddSingleton<ILocalizationService>(Service(platform)),
            static _ => { }); // REL.0E: the process UI culture is asserted in AppFormattingCultureTests

        Assert.Equal(["en-US"], platform.Writes);
        Assert.Equal("en-US", host.Services.GetRequiredService<ILocalizationService>().CurrentLanguageOverride);
    }

    [Fact]
    public void TheLaunchHost_InitializesTheLanguageExactlyOnce_BeforeReturning()
    {
        var recorder = new RecordingLocalization();

        using var host = App.BuildServicesHost(services => services.AddSingleton<ILocalizationService>(recorder), static _ => { });

        Assert.Equal(1, recorder.Initializations);
        Assert.Same(recorder, host.Services.GetRequiredService<ILocalizationService>());
    }

    [Fact]
    public void TheProductionComposition_RegistersTheRealLocalizationService()
    {
        var descriptor = Assert.Single(IsolatedAppComposition.ProductionDescriptors(), d => d.ServiceType == typeof(ILocalizationService));

        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(LocalizationService), descriptor.ImplementationType);
    }

    private static LocalizationService Service(FakeUiLanguagePlatform platform) =>
        new(NullLogger<LocalizationService>.Instance, platform, new FakeLanguageSettingsStore());

    private sealed class RecordingLocalization : ILocalizationService
    {
        public int Initializations { get; private set; }

        public string? CurrentLanguageOverride => null;

        public string GetString(string resourceKey) => resourceKey;

        public void InitializeFromSystem() => Initializations++;

        public void SetLanguage(string? languageTag)
        {
        }
    }
}
