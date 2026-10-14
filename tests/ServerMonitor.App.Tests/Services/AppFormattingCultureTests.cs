using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// The tests that change the PROCESS UI culture (<see cref="CultureInfo.DefaultThreadCurrentUICulture"/>) share this
/// collection, so xUnit never runs them alongside a test that formats with the default culture. Every one restores it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessUiCultureTests
{
    public const string Name = "process-ui-culture";
}

/// <summary>
/// REL.0E (Sentry, number format): numbers and dates format in the app's UI language, not the Windows display language,
/// while the language decisions keep reading the Windows culture. Never the user's real language override.
/// </summary>
[Collection(ProcessUiCultureTests.Name)]
public sealed class AppFormattingCultureTests
{
    private const long SixPointSixGiB = 7_086_696_038;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("!!")]
    public void NoUsableOverride_AppliesNothing(string? languageOverride)
    {
        var applied = new List<CultureInfo>();

        AppFormattingCulture.Apply(languageOverride, applied.Add);

        Assert.Null(AppFormattingCulture.For(languageOverride));
        Assert.Empty(applied);
    }

    [Theory]
    [InlineData("en-US", "6.6 GB")]
    [InlineData("pt-PT", "6,6 GB")]
    [InlineData("pt-BR", "6,6 GB")]
    public void TheOverride_IsTheCultureTheBytesFormatIn(string languageOverride, string expected)
    {
        var applied = new List<CultureInfo>();
        AppFormattingCulture.Apply(languageOverride, applied.Add);

        using var scope = new UiCultureScope("pt-PT");
        CultureInfo.CurrentUICulture = Assert.Single(applied);

        Assert.Equal(languageOverride, applied[0].Name);
        Assert.Equal(expected, MetricVisualPresentation.FormatBytes(SixPointSixGiB));
    }

    /// <summary>The defect as the human saw it: English chosen on a Portuguese (Brazil) Windows read "6,6 GB".</summary>
    [Fact]
    public void TheLaunchHost_FormatsInTheAppLanguage_OnAnotherWindowsLanguage()
    {
        using var scope = new UiCultureScope("pt-BR");
        Assert.Equal("6,6 GB", MetricVisualPresentation.FormatBytes(SixPointSixGiB));
        var platform = new FakeUiLanguagePlatform("pt-BR", "en-US");

        using var host = App.BuildServicesHost(services => services.AddSingleton<ILocalizationService>(Service(platform)));

        Assert.Equal("6.6 GB", MetricVisualPresentation.FormatBytes(SixPointSixGiB));
        Assert.Equal("en-US", CultureInfo.CurrentUICulture.Name);
        Assert.Equal("en-US", CultureInfo.DefaultThreadCurrentUICulture?.Name);
        Assert.Equal("pt-BR", platform.SystemUiCultureName);
        Assert.Empty(platform.Writes);
    }

    /// <summary>No override: the Windows display language already is the UI language, and nothing is changed.</summary>
    [Fact]
    public void TheLaunchHost_ChangesNothing_WhenTheSystemLanguageNeedsNoOverride()
    {
        using var scope = new UiCultureScope("pt-PT");

        using var host = App.BuildServicesHost(services =>
            services.AddSingleton<ILocalizationService>(Service(new FakeUiLanguagePlatform("pt-PT"))));

        Assert.Equal("pt-PT", CultureInfo.CurrentUICulture.Name);
        Assert.Null(CultureInfo.DefaultThreadCurrentUICulture);
        Assert.Equal("6,6 GB", MetricVisualPresentation.FormatBytes(SixPointSixGiB));
    }

    /// <summary>The culture is applied AFTER the startup resolution: a first run's own override is the one used.</summary>
    [Fact]
    public void TheLaunchHost_AppliesTheLanguageTheStartupResolutionWrote()
    {
        var applied = new List<CultureInfo>();
        var platform = new FakeUiLanguagePlatform("fr-FR");

        using var host = App.BuildServicesHost(
            services => services.AddSingleton<ILocalizationService>(Service(platform)),
            applied.Add);

        Assert.Equal(["en-US"], platform.Writes);
        Assert.Equal("en-US", Assert.Single(applied).Name);
    }

    /// <summary>
    /// The regression guard of the companion change: the real platform keeps the Windows culture it saw when it was
    /// built, so the recovery hint and "Use system language" never read the app language back as "the system".
    /// </summary>
    [Fact]
    public void TheRealPlatform_KeepsTheWindowsCulture_AfterTheAppLanguageIsApplied()
    {
        using var scope = new UiCultureScope("pt-BR");
        var platform = new LocalizationService.WindowsUiLanguagePlatform();

        AppFormattingCulture.SetProcessUiCulture(CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal("en-US", CultureInfo.CurrentUICulture.Name);
        Assert.Equal("pt-BR", platform.SystemUiCultureName);
    }

    [Fact]
    public void SetProcessUiCulture_SetsTheCurrentAndTheDefaultUiCulture_ButNotTheRegionalFormat()
    {
        using var scope = new UiCultureScope("pt-PT");
        var regional = CultureInfo.CurrentCulture;

        AppFormattingCulture.SetProcessUiCulture(CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal("en-US", CultureInfo.CurrentUICulture.Name);
        Assert.Equal("en-US", CultureInfo.DefaultThreadCurrentUICulture?.Name);
        Assert.Same(regional, CultureInfo.CurrentCulture);
    }

    private static LocalizationService Service(FakeUiLanguagePlatform platform) =>
        new(NullLogger<LocalizationService>.Instance, platform, new FakeLanguageSettingsStore());

    /// <summary>A Windows UI culture for the test, and the process UI culture exactly as it was afterwards.</summary>
    private sealed class UiCultureScope : IDisposable
    {
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
        private readonly CultureInfo? _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

        public UiCultureScope(string windowsUiCulture)
        {
            CultureInfo.DefaultThreadCurrentUICulture = null;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(windowsUiCulture);
        }

        public void Dispose()
        {
            CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }
}
