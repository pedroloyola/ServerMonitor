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

    /// <summary>Sentry nit: the user's Region customisations apply (only for their own regional format), read-only.</summary>
    [Fact]
    public void TheCulture_KeepsUserCustomisations_AndIsReadOnly()
    {
        var culture = AppFormattingCulture.For("en-US")!;

        Assert.True(culture.UseUserOverride);
        Assert.True(culture.IsReadOnly);
    }

    /// <summary>
    /// Fix round 1 (Cortex M-1), through the REAL service and the production setter: a regional variant of the Windows
    /// language keeps the Windows format; another language, or the user's own choice, formats in the override. History
    /// formats in the same culture as the process (Cortex L-1).
    /// </summary>
    [Theory]
    [InlineData("en-GB", null, false, null, "6.6 GB", "14/10/2026")]
    [InlineData("en-ZA", null, false, null, "6,6 GB", null)]
    [InlineData("pt-AO", null, false, null, "6,6 GB", null)]
    [InlineData("fr-FR", null, false, "en-US", "6.6 GB", "10/14/2026")]
    [InlineData("en-GB", "en-US", true, "en-US", "6.6 GB", "10/14/2026")]
    [InlineData("pt-BR", "pt-PT", true, "pt-PT", "6,6 GB", null)]
    [InlineData("pt-PT", "pt-BR", false, "pt-BR", "6,6 GB", null)] // v1.x legacy pt-BR, no record: the user's choice (#55)
    public void TheLaunchHost_FormatsInTheDecidedCulture(
        string windows, string? persisted, bool explicitRecord, string? applied, string bytes, string? shortDate)
    {
        using var scope = new UiCultureScope(windows);
        var store = explicitRecord ? FakeLanguageSettingsStore.ExplicitChoice() : new FakeLanguageSettingsStore();
        var service = new LocalizationService(
            NullLogger<LocalizationService>.Instance, new FakeUiLanguagePlatform(windows, persisted), store);

        using var host = App.BuildServicesHost(services => services.AddSingleton<ILocalizationService>(service));

        Assert.Equal(applied, service.FormattingCultureName);
        Assert.Equal(applied ?? windows, CultureInfo.CurrentUICulture.Name);
        Assert.Equal(applied, CultureInfo.DefaultThreadCurrentUICulture?.Name);
        Assert.Equal(bytes, MetricVisualPresentation.FormatBytes(SixPointSixGiB));
        if (shortDate is not null)
        {
            Assert.Equal(shortDate, new DateTime(2026, 10, 14).ToString("d", CultureInfo.CurrentUICulture));
        }

        Assert.Equal(CultureInfo.CurrentUICulture.Name, HistoryPresentation.FormatCulture(service.FormattingCultureName).Name);
    }

    /// <summary>Cortex L-1: History reads the service's formatting decision, never the raw override.</summary>
    [Fact]
    public void History_FormatsWithTheServicesFormattingDecision()
    {
        var code = Architecture.AppSourceTree.CodeWithoutComments("ViewModels/HistoryViewModel.cs");

        Assert.Contains("_localizationService?.FormattingCultureName", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentLanguageOverride", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cortex L-2: threads that never set a culture follow the process default when they READ it - a thread started
    /// before the launch applied the language, and a task whose context was captured before, both format in it.
    /// Run from a clean thread (no culture of its own, like the UI thread at startup).
    /// </summary>
    [Fact]
    public void ThreadsAndTasksStartedBeforeTheLaunch_FormatInTheAppliedCulture()
    {
        using var scope = new UiCultureScope("pt-BR");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("pt-BR"); // "Windows" for clean threads
        string? before = null, thread = null, task = null;

        var startup = new Thread(() =>
        {
            using var gate = new ManualResetEventSlim();
            var early = new Thread(() =>
            {
                gate.Wait();
                thread = MetricVisualPresentation.FormatBytes(SixPointSixGiB);
            });
            early.Start();
            var queued = Task.Run(() =>
            {
                gate.Wait();
                return MetricVisualPresentation.FormatBytes(SixPointSixGiB);
            });
            before = MetricVisualPresentation.FormatBytes(SixPointSixGiB);

            AppFormattingCulture.SetProcessUiCulture(CultureInfo.GetCultureInfo("en-US"));
            gate.Set();
            early.Join();
            task = queued.GetAwaiter().GetResult();
        });
        using (ExecutionContext.SuppressFlow())
        {
            startup.Start();
        }

        startup.Join();
        Assert.Equal("6,6 GB", before);
        Assert.Equal("6.6 GB", thread);
        Assert.Equal("6.6 GB", task);
    }

    /// <summary>
    /// Cortex N-1: production resolves the service - whose real platform takes the Windows snapshot in its constructor -
    /// before the culture is applied, and the production constructor builds that platform eagerly.
    /// </summary>
    [Fact]
    public void Production_TakesTheWindowsSnapshot_BeforeTheCultureIsApplied()
    {
        var code = Architecture.AppSourceTree.CodeWithoutComments("App.xaml.cs");
        var body = code[code.IndexOf("internal static IHost BuildServicesHost(", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("return host;", StringComparison.Ordinal)];
        var resolve = body.IndexOf("host.Services.GetRequiredService<ILocalizationService>()", StringComparison.Ordinal);
        var apply = body.IndexOf("AppFormattingCulture.Apply(localization.FormattingCultureName, applyUiCulture);", StringComparison.Ordinal);
        Assert.True(resolve >= 0 && apply > resolve, "the service (and its Windows snapshot) must be resolved before Apply");

        using var scope = new UiCultureScope("pt-BR");
        var service = new LocalizationService(NullLogger<LocalizationService>.Instance, new FakeLanguageSettingsStore());
        AppFormattingCulture.SetProcessUiCulture(CultureInfo.GetCultureInfo("en-US"));

        var platform = typeof(LocalizationService)
            .GetField("_platform", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service);
        Assert.Equal("pt-BR", Assert.IsType<LocalizationService.WindowsUiLanguagePlatform>(platform).SystemUiCultureName);
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
