using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Tests.TestSupport;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.Core.Domain;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// REL.0D language recovery (human GO D-B1 + D-B2), through the REAL <see cref="LocalizationService"/> over a fake
/// Windows platform (never the user's language preferences) and either a fake provenance store or the real
/// <see cref="JsonLanguageSettingsStore"/> on a temp folder (never the real <c>%LOCALAPPDATA%\ServerMonitor</c>).
/// A1-A7 (persistence and provenance), K7 (write order), the real launch path (<see cref="App.BuildServicesHost"/>), and
/// B1-B2 (the Overview hint's real handlers and its copy in the three languages).
/// </summary>
public sealed class LanguageRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sm-language-recovery-" + Guid.NewGuid().ToString("N"));

    // REL.0E: the launch host's process UI culture, recorded instead of changed under the parallel suite.
    private readonly List<string> _formattingCultures = [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---- A1: v1.x legacy - never reinterpreted, never rewritten ------------------------------------------------------

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en-GB")]
    [InlineData("pt-AO")]
    [InlineData("pt-MZ")]
    [InlineData("pt-CV")]
    public void A1_TheLegacyPtBr_IsKept_AtStartup_WithoutAnyWrite(string systemCulture)
    {
        var platform = new FakeUiLanguagePlatform(systemCulture, "pt-BR");
        var store = new FakeLanguageSettingsStore();
        var service = Service(platform, store);

        service.InitializeFromSystem();

        Assert.Empty(platform.Writes);
        Assert.Empty(store.Writes);
        Assert.Equal("pt-BR", service.CurrentLanguageOverride);
        Assert.False(service.FollowsSystemLanguage);
    }

    // ---- A2: "Use system language" persists (Cortex N-1) -------------------------------------------------------------

    [Theory]
    [InlineData("fr-FR", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("pt-AO", "pt-PT")]
    [InlineData("pt-MZ", "pt-PT")]
    [InlineData("pt-CV", "pt-PT")]
    public void A2_UseSystemLanguage_IsRecorded_AndReadsBackAsTheSystemChoice(string systemCulture, string expected)
    {
        var platform = new FakeUiLanguagePlatform(systemCulture, "pt-BR");
        var store = new FakeLanguageSettingsStore();

        Assert.True(Service(platform, store).TrySetLanguage(null));

        Assert.Equal([expected], platform.Writes);
        Assert.Equal([LanguageProvenance.System(expected)], store.Writes);
        var nextLaunch = Service(platform, store);
        nextLaunch.InitializeFromSystem();
        Assert.True(nextLaunch.FollowsSystemLanguage);
        Assert.Equal([expected], platform.Writes); // the next launch writes nothing more
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void A2_UseSystemLanguage_OnAnExactlySupportedCulture_ClearsTheOverride(string systemCulture)
    {
        var platform = new FakeUiLanguagePlatform(systemCulture, "en-US");
        var store = new FakeLanguageSettingsStore();
        var service = Service(platform, store);

        Assert.True(service.TrySetLanguage(null));

        Assert.Equal([""], platform.Writes);
        Assert.Null(service.CurrentLanguageOverride);
        Assert.True(service.FollowsSystemLanguage);
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("pt-PT")]
    [InlineData("en-US")]
    public void A2_AnExplicitChoice_IsRecordedAsExplicit(string choice)
    {
        var platform = new FakeUiLanguagePlatform("fr-FR");
        var store = new FakeLanguageSettingsStore();
        var service = Service(platform, store);

        Assert.True(service.TrySetLanguage(choice));

        Assert.Equal([choice], platform.Writes);
        Assert.Equal([LanguageProvenance.Explicit], store.Writes);
        Assert.False(service.FollowsSystemLanguage);
    }

    /// <summary>English chosen explicitly on a Windows that maps to English is still a choice, not "the system".</summary>
    [Fact]
    public void A2_ExplicitEnglish_OnAnEnglishMappedWindows_IsNotTheSystemChoice()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR");
        var store = new FakeLanguageSettingsStore();
        var service = Service(platform, store);
        service.InitializeFromSystem(); // first run: en-US, recorded as the system's

        Assert.True(service.TrySetLanguage("en-US"));

        Assert.False(Service(platform, store).FollowsSystemLanguage);
        platform.SystemUiCultureName = "pt-PT";
        Service(platform, store).InitializeFromSystem();
        Assert.Equal("en-US", platform.PrimaryLanguageOverride);
    }

    // ---- A3: the app's own override follows Windows ----------------------------------------------------------------

    [Theory]
    [InlineData("pt-AO", "pt-PT")]
    [InlineData("pt-PT", "")]
    [InlineData("en-US", "")]
    public void A3_TheSystemChoice_FollowsALaterWindowsChange(string newSystemCulture, string expected)
    {
        var platform = new FakeUiLanguagePlatform("fr-FR");
        var store = new FakeLanguageSettingsStore();
        Service(platform, store).InitializeFromSystem();
        Assert.Equal("en-US", platform.PrimaryLanguageOverride);

        platform.SystemUiCultureName = newSystemCulture;
        var nextLaunch = Service(platform, store);
        nextLaunch.InitializeFromSystem();

        Assert.Equal(["en-US", expected], platform.Writes);
        Assert.Equal(LanguageRecordRead.Of(LanguageProvenance.System(expected)), store.State);
        Assert.True(nextLaunch.FollowsSystemLanguage);
    }

    // ---- A4: an explicit choice never follows Windows --------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "pt-PT")]
    [InlineData("pt-BR", "fr-FR")]
    [InlineData("pt-PT", "en-GB")]
    public void A4_AnExplicitChoice_IsNeverReResolved(string choice, string newSystemCulture)
    {
        var platform = new FakeUiLanguagePlatform("fr-FR", choice);
        var store = FakeLanguageSettingsStore.ExplicitChoice();
        platform.SystemUiCultureName = newSystemCulture;

        Service(platform, store).InitializeFromSystem();

        Assert.Empty(platform.Writes);
        Assert.Empty(store.Writes);
    }

    /// <summary>
    /// K3: a downgrade to v1.1.x leaves the record but lets the user pick another language there; back on this version that
    /// override differs from the recorded one, so it is the user's choice and never follows Windows.
    /// </summary>
    [Theory]
    [InlineData("pt-PT", "en-US", "fr-FR")]
    [InlineData("pt-BR", "en-US", "pt-AO")]
    [InlineData("en-US", "pt-PT", "pt-MZ")]
    public void A4_AnOverrideThatDiffersFromTheRecord_IsTheUsersChoice(string persisted, string recorded, string systemCulture)
    {
        var platform = new FakeUiLanguagePlatform(systemCulture, persisted);
        var store = FakeLanguageSettingsStore.Following(recorded);
        var service = Service(platform, store);

        service.InitializeFromSystem();

        Assert.Empty(platform.Writes);
        Assert.Empty(store.Writes);
        Assert.False(service.FollowsSystemLanguage);
    }

    // ---- A5: corrupt provenance proves nothing -----------------------------------------------------------------------

    [Theory]
    [InlineData("pt-BR", "fr-FR")]
    [InlineData("en-US", "pt-PT")]
    [InlineData("pt-PT", "en-US")]
    public void A5_AnUnreadableRecord_KeepsTheOverride_WritesNothing_AndOffersNoHint(string persisted, string systemCulture)
    {
        var platform = new FakeUiLanguagePlatform(systemCulture, persisted);
        var store = FakeLanguageSettingsStore.Unreadable();
        var service = Service(platform, store);

        service.InitializeFromSystem();

        Assert.Empty(platform.Writes);
        Assert.Empty(store.Writes);
        Assert.Null(service.PendingLegacyLanguageHint);
        Assert.False(service.FollowsSystemLanguage);
    }

    [Fact]
    public void A5_AnUnreadableRecord_WithoutAnOverride_ResolvesAsBefore_AndNeverRewritesTheRecord()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR");
        var store = FakeLanguageSettingsStore.Unreadable();

        Service(platform, store).InitializeFromSystem();

        Assert.Equal(["en-US"], platform.Writes);
        Assert.Empty(store.Writes);
    }

    // ---- A6 / K7: the record goes first; without it the override is never written ------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("pt-PT")]
    [InlineData("en-US")]
    public void A6_WhenTheRecordCannotBeSaved_TheLanguageIsNotChanged(string? choice)
    {
        var platform = new FakeUiLanguagePlatform("fr-FR", "pt-BR");
        var store = new FakeLanguageSettingsStore { FailWrites = true };
        var service = Service(platform, store);

        Assert.False(service.TrySetLanguage(choice));
        Assert.False(service.KeepCurrentLanguage());

        Assert.Empty(platform.Writes);
        Assert.Equal("pt-BR", service.CurrentLanguageOverride);
    }

    [Fact]
    public void A6_AtStartup_WithoutASavedRecord_NoOverrideIsWritten()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR");
        var store = new FakeLanguageSettingsStore { FailWrites = true };

        Service(platform, store).InitializeFromSystem();

        Assert.Empty(platform.Writes);
    }

    [Fact]
    public void K7_TheRecordIsAlwaysWrittenBeforeTheOverride()
    {
        var journal = new List<string>();
        var platform = new FakeUiLanguagePlatform("fr-FR", journal: journal);
        var store = new FakeLanguageSettingsStore(journal: journal);
        var service = Service(platform, store);

        service.InitializeFromSystem();
        service.TrySetLanguage("pt-PT");
        service.TrySetLanguage(null);
        platform.SystemUiCultureName = "pt-AO";
        Service(platform, store).InitializeFromSystem();

        Assert.Equal(
            [
                "record:True:en-US", "override:en-US",
                "record:False:", "override:pt-PT",
                "record:True:en-US", "override:en-US",
                "record:True:pt-PT", "override:pt-PT"
            ],
            journal);
    }

    // ---- A7: the real launch path and the real file -------------------------------------------------------------------

    [Fact]
    public void A7_TheLaunchHost_RecordsTheFirstRun_AndTheNextLaunchKeepsTheSystemChoice()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR");

        using (App.BuildServicesHost(services => services.AddSingleton<ILocalizationService>(Service(platform, FileStore())), RecordCulture))
        {
        }

        Assert.Equal(LanguageRecordRead.Of(LanguageProvenance.System("en-US")), FileStore().Read());
        platform.SystemUiCultureName = "pt-AO";
        using var next = App.BuildServicesHost(services => services.AddSingleton<ILocalizationService>(Service(platform, FileStore())), RecordCulture);

        Assert.Equal(["en-US", "pt-PT"], platform.Writes);
        Assert.True(next.Services.GetRequiredService<ILocalizationService>().FollowsSystemLanguage);
        // REL.0E fix round 1 (Cortex M-1): fr-FR formats in en-US, the language it shows; on pt-AO the app's own pt-PT is
        // a regional variant of the Windows language, which keeps its own format - nothing is applied.
        Assert.Equal(["en-US"], _formattingCultures);
    }

    [Fact]
    public void A7_TheLaunchHost_NeverTouchesALegacyPtBr_AndLeavesNoFile()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR", "pt-BR");

        using var host = App.BuildServicesHost(services => services.AddSingleton<ILocalizationService>(Service(platform, FileStore())), RecordCulture);

        Assert.Empty(platform.Writes);
        Assert.Equal(LanguageRecordRead.Absent, FileStore().Read());
        Assert.Equal("en-US", host.Services.GetRequiredService<ILocalizationService>().PendingLegacyLanguageHint);
        Assert.Equal(["pt-BR"], _formattingCultures); // the legacy pt-BR is the language shown until the user decides
    }

    [Fact]
    public void A7_TheProductionComposition_RegistersTheRealStore_OnTheCurrentUsersPath()
    {
        var descriptors = IsolatedAppComposition.ProductionDescriptors();

        var store = Assert.Single(descriptors, d => d.ServiceType == typeof(ILanguageSettingsStore));
        Assert.Equal(typeof(JsonLanguageSettingsStore), store.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, store.Lifetime);
        // The production path, read from the root's own registration (never constructed here): next to the other settings.
        var language = Assert.IsType<LanguageSettingsStorageOptions>(
            Assert.Single(descriptors, d => d.ServiceType == typeof(LanguageSettingsStorageOptions)).ImplementationInstance).FilePath;
        var notification = Assert.IsType<NotificationSettingsStorageOptions>(
            Assert.Single(descriptors, d => d.ServiceType == typeof(NotificationSettingsStorageOptions)).ImplementationInstance).FilePath;
        Assert.Equal("language-settings.json", Path.GetFileName(language));
        Assert.Equal(Path.GetDirectoryName(notification), Path.GetDirectoryName(language));
        var hint = Assert.Single(descriptors, d => d.ServiceType == typeof(LanguageRecoveryHintViewModel));
        Assert.Equal(ServiceLifetime.Singleton, hint.Lifetime);
        Assert.Contains(
            typeof(DashboardPage).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(LanguageRecoveryHintViewModel));
    }

    [Fact]
    public void A7_TheIsolatedComposition_PutsTheFileUnderTheTestRoot()
    {
        using var composition = new IsolatedAppComposition();
        using var provider = composition.BuildProvider();

        var options = provider.GetRequiredService<LanguageSettingsStorageOptions>();
        Assert.Equal(Path.Combine(composition.DataDirectory, "language-settings.json"), options.FilePath);
        Assert.IsType<JsonLanguageSettingsStore>(provider.GetRequiredService<ILanguageSettingsStore>());
    }

    // ---- B1: the Overview hint, through its real handlers -----------------------------------------------------------

    [Theory]
    [InlineData("fr-FR", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("pt-AO", "pt-PT")]
    [InlineData("pt-MZ", "pt-PT")]
    [InlineData("pt-CV", "pt-PT")]
    public void B1_UseTheSystemLanguage_IsExactlySettingsSystemChoice_ThenTheRestartNotice_AndNeverAgain(string systemCulture, string expected)
    {
        var platform = new FakeUiLanguagePlatform(systemCulture, "pt-BR");
        var store = new FakeLanguageSettingsStore();
        var hint = Hint(platform, store);
        Assert.True(hint.IsOpen);
        Assert.True(hint.HasVisibleNotice);
        Assert.Equal(expected, hint.TargetLanguage);

        hint.UseSystemLanguageCommand.Execute(null);

        Assert.False(hint.IsOpen);
        Assert.True(hint.IsRestartNoticeOpen);
        Assert.True(hint.HasVisibleNotice);
        hint.IsRestartNoticeOpen = false;
        Assert.False(hint.HasVisibleNotice);
        Assert.False(hint.IsSaveErrorOpen);
        Assert.Equal([expected], platform.Writes);
        Assert.Equal([LanguageProvenance.System(expected)], store.Writes);
        Assert.False(Hint(platform, store).IsOpen);
        Assert.True(Service(platform, store).FollowsSystemLanguage);
    }

    [Fact]
    public void B1_Keep_RecordsPtBrAsTheUsersChoice_WithoutChangingIt_AndNeverAgain()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR", "pt-BR");
        var store = new FakeLanguageSettingsStore();
        var hint = Hint(platform, store);

        hint.KeepCurrentLanguageCommand.Execute(null);

        Assert.False(hint.IsOpen);
        Assert.False(hint.IsRestartNoticeOpen);
        Assert.Empty(platform.Writes);
        Assert.Equal([LanguageProvenance.Explicit], store.Writes);
        Assert.False(Hint(platform, store).IsOpen);
        Assert.Equal("pt-BR", Service(platform, store).CurrentLanguageOverride);
    }

    [Fact]
    public void B1_WhenTheDecisionCannotBeSaved_NothingChanges_TheHintStays_AndAnErrorSaysSo()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR", "pt-BR");
        var store = new FakeLanguageSettingsStore { FailWrites = true };
        var hint = Hint(platform, store);

        hint.UseSystemLanguageCommand.Execute(null);
        Assert.True(hint.IsOpen);
        Assert.True(hint.IsSaveErrorOpen);
        Assert.False(hint.IsRestartNoticeOpen);

        hint.IsSaveErrorOpen = false;
        hint.KeepCurrentLanguageCommand.Execute(null);
        Assert.True(hint.IsOpen);
        Assert.True(hint.IsSaveErrorOpen);

        Assert.Empty(platform.Writes);
        Assert.Equal("pt-BR", platform.PrimaryLanguageOverride);

        store.FailWrites = false; // a retry that succeeds closes the error with the hint
        hint.KeepCurrentLanguageCommand.Execute(null);
        Assert.False(hint.IsOpen);
        Assert.False(hint.IsSaveErrorOpen);
    }

    [Theory]
    [InlineData("pt-BR", "en-US")] // an explicit choice on a supported Windows
    [InlineData("pt-BR", "pt-PT")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("en-US", "fr-FR")]
    [InlineData(null, "fr-FR")]
    public void B1_TheHintIsClosed_OutsideTheLegacySignature(string? persisted, string systemCulture)
    {
        var platform = new FakeUiLanguagePlatform(systemCulture, persisted);
        var hint = Hint(platform, new FakeLanguageSettingsStore());

        Assert.False(hint.IsOpen);
        Assert.False(hint.HasVisibleNotice);
    }

    [Fact]
    public void B1_TheHintIsClosed_WhenTheRecordIsUnreadable_OrAlreadyDecided()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR", "pt-BR");

        Assert.False(Hint(platform, FakeLanguageSettingsStore.Unreadable()).IsOpen);
        Assert.False(Hint(platform, FakeLanguageSettingsStore.ExplicitChoice()).IsOpen);
    }

    /// <summary>The startup path never retires the hint by itself (the legacy override writes no record at startup).</summary>
    [Fact]
    public void B1_TheHintSurvivesRestarts_UntilTheUserDecides()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR", "pt-BR");
        var store = new FakeLanguageSettingsStore();

        for (var launch = 0; launch < 3; launch++)
        {
            Service(platform, store).InitializeFromSystem();
            Assert.True(Hint(platform, store).IsOpen);
        }
    }

    [Fact]
    public void B1_ACommandAfterTheDecision_DoesNothing()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR", "pt-BR");
        var store = new FakeLanguageSettingsStore();
        var hint = Hint(platform, store);

        hint.KeepCurrentLanguageCommand.Execute(null);
        hint.UseSystemLanguageCommand.Execute(null);

        Assert.Empty(platform.Writes);
        Assert.Single(store.Writes);
    }

    // ---- B3 (PR #55 fix round 1, Relay L-1): a decision taken elsewhere retires the singleton hint ----------------

    [Theory]
    [InlineData("pt-PT")]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public void B3_AnExplicitChoiceInSettings_ClosesTheHintOnTheNextVisit_WithoutWritingAgain(string choice)
    {
        var platform = new FakeUiLanguagePlatform("fr-FR", "pt-BR");
        var store = new FakeLanguageSettingsStore();
        var hint = Hint(platform, store);
        Assert.True(hint.IsOpen);

        Assert.True(Service(platform, store).TrySetLanguage(choice)); // Settings › Language, same session
        hint.Refresh();                                                 // back on Overview

        Assert.False(hint.IsOpen);
        Assert.False(hint.IsRestartNoticeOpen);
        Assert.False(hint.HasVisibleNotice);
        Assert.Equal([choice], platform.Writes);
        Assert.Equal([LanguageProvenance.Explicit], store.Writes);
    }

    /// <summary>Even without a page visit, an action on a hint that is no longer due changes nothing and just closes it.</summary>
    [Fact]
    public void B3_TheHintsActions_RecheckFirst_AndNeverOverrideALaterChoice()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR", "pt-BR");
        var store = new FakeLanguageSettingsStore();
        var useSystem = Hint(platform, store);
        var keep = Hint(platform, store);
        Assert.True(Service(platform, store).TrySetLanguage("pt-PT"));

        useSystem.UseSystemLanguageCommand.Execute(null);
        keep.KeepCurrentLanguageCommand.Execute(null);

        Assert.False(useSystem.IsOpen);
        Assert.False(useSystem.IsRestartNoticeOpen);
        Assert.False(keep.IsOpen);
        Assert.Equal(["pt-PT"], platform.Writes);
        Assert.Single(store.Writes);
    }

    [Fact]
    public void B3_RefreshKeepsADueHint_AndNeverReopensAClosedOne()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR", "pt-BR");
        var store = new FakeLanguageSettingsStore();
        var hint = Hint(platform, store);

        hint.Refresh();
        Assert.True(hint.IsOpen);

        hint.KeepCurrentLanguageCommand.Execute(null);
        store.State = LanguageRecordRead.Absent; // even if the record vanished, the closed hint stays closed this session
        hint.Refresh();
        Assert.False(hint.IsOpen);
    }

    [Fact]
    public void B3_TheOverviewRefreshesTheHint_OnEveryVisit()
    {
        var code = Architecture.AppSourceTree.CodeWithoutComments("Views/DashboardPage.xaml.cs");
        var loaded = code[code.IndexOf("private async void OnLoaded(", StringComparison.Ordinal)..];
        loaded = loaded[..loaded.IndexOf("RestoreFocus);", StringComparison.Ordinal)];

        Assert.Contains("LanguageHint.Refresh();", loaded, StringComparison.Ordinal);
    }

    // ---- C (PR #55 fix round 1, Cortex N-1): identities sharing one real folder -----------------------------------------

    /// <summary>
    /// The dev-machine case: the Store package has an explicit en-US (no record of its own), an unpackaged run without an
    /// override records "System(en-US)" in the same real folder. The Store package must NOT take that as its own
    /// automatic value: when Windows changes, its explicit en-US stays.
    /// </summary>
    [Fact]
    public void C1_AnotherIdentitysRecord_NeverMakesThisOverrideAutomatic()
    {
        var unpackaged = new FakeUiLanguagePlatform("fr-FR");
        Service(unpackaged, FileStore("unpackaged")).InitializeFromSystem();
        Assert.Equal(LanguageRecordRead.Of(LanguageProvenance.System("en-US")), FileStore("unpackaged").Read());

        var store = new FakeUiLanguagePlatform("pt-PT", "en-US"); // Windows changed; the Store install chose English
        var storeService = Service(store, FileStore("package:Store.Family_8wekyb3d8bbwe"));
        storeService.InitializeFromSystem();

        Assert.Empty(store.Writes);
        Assert.False(storeService.FollowsSystemLanguage);
    }

    /// <summary>Same file, same identity: the app's own automatic value still follows Windows (no regression of A3).</summary>
    [Fact]
    public void C2_TheSameIdentitysRecord_StillFollowsWindows()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR");
        Service(platform, FileStore("package:Store.Family_8wekyb3d8bbwe")).InitializeFromSystem();

        platform.SystemUiCultureName = "pt-AO";
        Service(platform, FileStore("package:Store.Family_8wekyb3d8bbwe")).InitializeFromSystem();

        Assert.Equal(["en-US", "pt-PT"], platform.Writes);
    }

    /// <summary>A legacy pt-BR of this identity still gets its one-time hint when only another identity has a record.</summary>
    [Fact]
    public void C3_TheLegacyHint_IsNotSuppressedByAnotherIdentitysRecord()
    {
        Service(new FakeUiLanguagePlatform("en-US"), FileStore("unpackaged")).InitializeFromSystem();

        var store = Service(new FakeUiLanguagePlatform("fr-FR", "pt-BR"), FileStore("package:Store.Family_8wekyb3d8bbwe"));

        Assert.Equal("en-US", store.PendingLegacyLanguageHint);
    }

    // ---- B2: the copy, in the three languages ---------------------------------------------------------------------

    [Theory]
    [InlineData("pt-BR", "fr-FR", "English")]
    [InlineData("pt-BR", "pt-AO", "Português (Portugal)")]
    [InlineData("pt-PT", "fr-FR", "English")]
    [InlineData("en-US", "pt-MZ", "Português (Portugal)")]
    public void B2_TheMessage_NamesTheEffectiveLanguage_WithThePickersName(string uiCulture, string systemCulture, string languageName)
    {
        var platform = new FakeUiLanguagePlatform(systemCulture, "pt-BR");
        var strings = new ResWLocalizationService(uiCulture);
        var hint = new LanguageRecoveryHintViewModel(new ResWStrings(Service(platform, new FakeLanguageSettingsStore()), strings));

        Assert.Equal(strings.GetString("LanguageRecoveryHintTitle"), hint.Title);
        Assert.Contains(languageName, hint.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", hint.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void B2_TheKeysExistInEveryLanguage_WithExactlyOnePlaceholder()
    {
        string[] keys =
        [
            "LanguageRecoveryHintTitle", "LanguageRecoveryHintMessageFormat", "LanguageRecoveryHintUseSystemButton.Content",
            "LanguageRecoveryHintKeepButton.Content", "LanguageRecoveryHintSaveError.Title", "LanguageRecoveryHintSaveError.Message"
        ];
        foreach (var culture in ResWLocalizationService.Cultures)
        {
            var strings = new ResWLocalizationService(culture);
            Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(strings.GetString(key)), $"{culture}: {key}"));
            var format = strings.GetString("LanguageRecoveryHintMessageFormat");
            Assert.Contains("{0}", format, StringComparison.Ordinal);
            Assert.DoesNotContain("{1}", format, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("pt-PT", "Utilizar o idioma do sistema", "Manter português (Brasil)")]
    [InlineData("pt-BR", "Usar o idioma do sistema", "Manter português (Brasil)")]
    public void B2_TheActionsUseTheHumanWording(string culture, string useSystem, string keep)
    {
        var strings = new ResWLocalizationService(culture);

        Assert.Equal(useSystem, strings.GetString("LanguageRecoveryHintUseSystemButton.Content"));
        Assert.Equal(keep, strings.GetString("LanguageRecoveryHintKeepButton.Content"));
    }

    private static LocalizationService Service(FakeUiLanguagePlatform platform, ILanguageSettingsStore store) =>
        new(NullLogger<LocalizationService>.Instance, platform, store);

    /// <summary>The hint over the real service; strings come from the en-US resw (the real ResourceLoader needs a package).</summary>
    private static LanguageRecoveryHintViewModel Hint(FakeUiLanguagePlatform platform, ILanguageSettingsStore store) =>
        new(new ResWStrings(Service(platform, store), new ResWLocalizationService("en-US")));

    private void RecordCulture(System.Globalization.CultureInfo culture) => _formattingCultures.Add(culture.Name);

    private JsonLanguageSettingsStore FileStore(string writer = "unpackaged") =>
        new(new LanguageSettingsStorageOptions { FilePath = Path.Combine(_directory, "language-settings.json"), Writer = writer },
            NullLogger<JsonLanguageSettingsStore>.Instance);

    /// <summary>The REAL service for every language decision; only <see cref="GetString"/> reads a resw file.</summary>
    private sealed class ResWStrings(LocalizationService real, ResWLocalizationService strings) : ILocalizationService
    {
        public string? CurrentLanguageOverride => real.CurrentLanguageOverride;

        public bool FollowsSystemLanguage => real.FollowsSystemLanguage;

        public string? PendingLegacyLanguageHint => real.PendingLegacyLanguageHint;

        public string GetString(string resourceKey) => strings.GetString(resourceKey);

        public void InitializeFromSystem() => real.InitializeFromSystem();

        public void SetLanguage(string? languageTag) => real.SetLanguage(languageTag);

        public bool TrySetLanguage(string? languageTag) => real.TrySetLanguage(languageTag);

        public bool KeepCurrentLanguage() => real.KeepCurrentLanguage();
    }
}
