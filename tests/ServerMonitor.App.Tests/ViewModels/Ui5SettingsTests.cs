using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.5 §4: the Settings behaviours the split into two sub-pages touches, and the test gaps Cortex listed (theme and
/// language VMs, clear/reset history, hidden-server restore, reset ignored devices, idempotent/concurrent load).
/// Deterministic: gates are TaskCompletionSources, never delays; 30 s only as a deadlock guard.
/// </summary>
public sealed class Ui5SettingsTests
{
    private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(30);

    // ---- theme / language -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, AppThemePreference.Light)]
    [InlineData(2, AppThemePreference.Dark)]
    [InlineData(0, AppThemePreference.System)]
    public void ChoosingATheme_AppliesIt(int index, AppThemePreference expected)
    {
        var world = new World(theme: AppThemePreference.Light == expected ? AppThemePreference.Dark : AppThemePreference.Light);

        world.ViewModel.SelectedThemeIndex = index;

        Assert.Equal(new[] { expected }, world.Theme.Applied);
    }

    [Fact]
    public void AnUndefinedThemeIndex_IsNeverApplied_AndTheSameIndexIsANoOp()
    {
        var world = new World(theme: AppThemePreference.Dark);

        world.ViewModel.SelectedThemeIndex = 2; // already Dark
        world.ViewModel.SelectedThemeIndex = -1; // ComboBox "nothing selected"
        world.ViewModel.SelectedThemeIndex = 7;

        Assert.Empty(world.Theme.Applied);
    }

    [Fact]
    public void TheThemeIndex_StartsFromTheCurrentTheme()
    {
        Assert.Equal(2, new World(theme: AppThemePreference.Dark).ViewModel.SelectedThemeIndex);
        Assert.Equal(1, new World(theme: AppThemePreference.Light).ViewModel.SelectedThemeIndex);
    }

    [Theory]
    [InlineData(1, "pt-BR")]
    [InlineData(2, "pt-PT")]
    [InlineData(3, "en-US")]
    [InlineData(0, null)]
    public void ChoosingALanguage_SetsTheOverride_AndAsksForARestart(int index, string? tag)
    {
        var world = new World(language: index == 0 ? "en-US" : null);

        world.ViewModel.SelectedLanguageIndex = index;

        Assert.Equal(new[] { tag }, world.Localization.Set);
        Assert.True(world.ViewModel.IsRestartNoticeOpen);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("pt-BR", 1)]
    [InlineData("pt-PT", 2)]
    [InlineData("en-US", 3)]
    public void TheLanguageIndex_StartsFromTheOverride_AndReselectingItChangesNothing(string? current, int index)
    {
        var world = new World(language: current);
        Assert.Equal(index, world.ViewModel.SelectedLanguageIndex);

        world.ViewModel.SelectedLanguageIndex = index;

        Assert.Empty(world.Localization.Set);
        Assert.False(world.ViewModel.IsRestartNoticeOpen);
    }

    /// <summary>
    /// REL.0C/REL.0D: on first run the picker shows how the app starts. With no persisted choice that is "Use system
    /// language" (0) on EVERY Windows culture - since REL.0D also where the startup wrote an override for it (Cortex N-1);
    /// a persisted choice without a provenance record (also the old pt-BR default) is shown as it is. The REAL
    /// LocalizationService startup over a fake platform and a fake provenance store (Atlas PR46-01).
    /// </summary>
    [Theory]
    [InlineData(null, "en-US", 0)]
    [InlineData(null, "pt-PT", 0)]
    [InlineData(null, "pt-BR", 0)]
    [InlineData(null, "en-GB", 0)]
    [InlineData(null, "fr-FR", 0)]
    [InlineData(null, "", 0)]
    [InlineData(null, "pt-AO", 0)]
    [InlineData("pt-BR", "fr-FR", 1)]
    [InlineData("pt-PT", "en-GB", 2)]
    [InlineData("en-US", "pt-AO", 3)]
    public void OnFirstRun_TheLanguageIndex_ShowsTheStartupLanguage(string? persisted, string systemCulture, int index)
    {
        var startup = RealLocalization(new FakeUiLanguagePlatform(systemCulture, persisted), new FakeLanguageSettingsStore());
        startup.InitializeFromSystem();

        Assert.Equal(index, new World(localization: startup).ViewModel.SelectedLanguageIndex);
    }

    /// <summary>REL.0D (Cortex N-1): "Use system language" sticks - the next Settings shows it again, not "English".</summary>
    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en-GB")]
    [InlineData("pt-AO")]
    public void ChoosingTheSystemLanguage_IsShownAgainNextTime(string systemCulture)
    {
        var platform = new FakeUiLanguagePlatform(systemCulture, "pt-PT");
        var store = new FakeLanguageSettingsStore();
        var world = new World(localization: RealLocalization(platform, store));
        Assert.Equal(2, world.ViewModel.SelectedLanguageIndex);

        world.ViewModel.SelectedLanguageIndex = 0;

        Assert.True(world.ViewModel.IsRestartNoticeOpen);
        var next = RealLocalization(platform, store);
        next.InitializeFromSystem();
        Assert.Equal(0, new World(localization: next).ViewModel.SelectedLanguageIndex);
    }

    /// <summary>REL.0D: a choice that could not be saved changes nothing - no restart notice, the picker goes back.</summary>
    [Fact]
    public void AChoiceThatCannotBeSaved_ShowsNoRestartNotice_AndThePickerGoesBack()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR", "pt-BR");
        var world = new World(localization: RealLocalization(platform, new FakeLanguageSettingsStore { FailWrites = true }));
        var raised = new List<string?>();
        world.ViewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        world.ViewModel.SelectedLanguageIndex = 3;

        Assert.Equal(1, world.ViewModel.SelectedLanguageIndex);
        Assert.False(world.ViewModel.IsRestartNoticeOpen);
        Assert.Empty(platform.Writes);
        Assert.Equal(2, raised.Count(name => name == nameof(SettingsViewModel.SelectedLanguageIndex)));
    }

    /// <summary>
    /// REL.0D (visual QA finding): Settings is a singleton, so a language decided on the Overview hint after Settings was
    /// first opened must show on the next visit - display only, no write, no restart notice.
    /// </summary>
    [Fact]
    public void ADecisionTakenElsewhere_IsShownOnTheNextVisit_WithoutWritingAgain()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR", "pt-BR");
        var store = new FakeLanguageSettingsStore();
        var service = RealLocalization(platform, store);
        var world = new World(localization: service);
        Assert.Equal(1, world.ViewModel.SelectedLanguageIndex);
        var raised = new List<string?>();
        world.ViewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(service.TrySetLanguage(null)); // the hint's "Use the system language"
        world.ViewModel.NotifyNavigatedTo();

        Assert.Equal(0, world.ViewModel.SelectedLanguageIndex);
        Assert.Contains(nameof(SettingsViewModel.SelectedLanguageIndex), raised);
        Assert.False(world.ViewModel.IsRestartNoticeOpen);
        Assert.Equal(["en-US"], platform.Writes);
        Assert.Single(store.Writes);

        raised.Clear();
        world.ViewModel.NotifyNavigatedTo(); // nothing changed since: no notification
        Assert.DoesNotContain(nameof(SettingsViewModel.SelectedLanguageIndex), raised);
    }

    // ---- REL.0D D-2 (VM QA): a picker with nothing selected is not "Use system language" --------------------------------

    /// <summary>
    /// The VM evidence, through the real Settings handler, the real store (a temp folder, never the user's) and the real
    /// launch path: first run System(""), Settings › Português (Portugal), relaunch, then the ComboBox pushes -1 back
    /// through its TwoWay binding. The explicit choice must survive - record, override and the next launch.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void D2_AnIndexThatIsNoItem_NeverReplacesAnExplicitChoice(int noItem)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sm-d2-" + Guid.NewGuid().ToString("N"));
        try
        {
            var platform = new FakeUiLanguagePlatform("pt-BR");
            var store = FileStore(directory);
            Launch(platform, store);
            Assert.Equal(LanguageRecordRead.Of(LanguageProvenance.System("")), store.Read()); // the VM's first run

            new World(localization: Real(platform, store)).ViewModel.SelectedLanguageIndex = 2; // Português (Portugal)
            Assert.Equal(LanguageRecordRead.Of(LanguageProvenance.Explicit), store.Read());

            var relaunched = Launch(platform, store);
            var settings = new World(localization: relaunched).ViewModel;
            Assert.Equal(2, settings.SelectedLanguageIndex);
            var raised = new List<string?>();
            settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            settings.SelectedLanguageIndex = noItem;

            Assert.Equal(LanguageRecordRead.Of(LanguageProvenance.Explicit), store.Read());
            Assert.Equal("pt-PT", platform.PrimaryLanguageOverride);
            Assert.Equal(["pt-PT"], platform.Writes);
            Assert.False(settings.IsRestartNoticeOpen);
            Assert.Equal(2, settings.SelectedLanguageIndex);
            Assert.Contains(nameof(SettingsViewModel.SelectedLanguageIndex), raised); // the picker is told again

            var next = Launch(platform, store);
            Assert.Equal("pt-PT", next.CurrentLanguageOverride);
            Assert.False(next.FollowsSystemLanguage);
            Assert.Equal(2, new World(localization: next).ViewModel.SelectedLanguageIndex);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>D-2: index 0 is still exactly "Use system language" - persisted as System, shown again next time.</summary>
    [Fact]
    public void D2_IndexZero_IsStillUseSystemLanguage()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR", "pt-PT");
        var store = FakeLanguageSettingsStore.ExplicitChoice();
        var settings = new World(localization: RealLocalization(platform, store)).ViewModel;

        settings.SelectedLanguageIndex = 0;

        Assert.Equal([LanguageProvenance.System("en-US")], store.Writes);
        Assert.Equal(["en-US"], platform.Writes);
        Assert.True(settings.IsRestartNoticeOpen);
        Assert.True(RealLocalization(platform, store).FollowsSystemLanguage);
    }

    /// <summary>D-2: an index that is no item changes nothing on a page that follows the system either.</summary>
    [Fact]
    public void D2_AnIndexThatIsNoItem_WritesNothing_WhenFollowingTheSystem()
    {
        var platform = new FakeUiLanguagePlatform("fr-FR");
        var store = new FakeLanguageSettingsStore();
        RealLocalization(platform, store).InitializeFromSystem();
        var settings = new World(localization: RealLocalization(platform, store)).ViewModel;
        var writes = (platform.Writes.Count, store.Writes.Count);

        settings.SelectedLanguageIndex = -1;

        Assert.Equal(writes, (platform.Writes.Count, store.Writes.Count));
        Assert.Equal(0, settings.SelectedLanguageIndex);
        Assert.False(settings.IsRestartNoticeOpen);
    }

    // ---- REL.0D D-2 root cause (VM reproduction, no keystrokes): the override read on a new launch ----------------------

    /// <summary>
    /// The VM reproduction through the real Settings handler, the real store (a temp folder) and the real launch path,
    /// over a platform with the Windows App SDK's packaged semantics (the SDK getter is process-local; Windows persists the
    /// value): Settings › Português (Portugal), then two relaunches. Before the fix the first relaunch read "" and recorded
    /// System("") over the explicit choice - the file the VM showed - while the UI stayed pt-PT; on an UNSUPPORTED Windows
    /// language it also wrote the system override over the choice.
    /// </summary>
    [Theory]
    [InlineData("pt-BR", "")]      // the VM: display pt-BR, nothing to write for the system language
    [InlineData("fr-FR", "en-US")] // unsupported display: the system override would replace the choice
    [InlineData("en-GB", "en-US")]
    public void D2_AnExplicitChoice_SurvivesEveryRelaunch_OfThePackagedApp(string display, string systemOverride)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sm-d2b-" + Guid.NewGuid().ToString("N"));
        try
        {
            var windows = new PackagedUiLanguagePlatform.PersistedOverride();
            var store = FileStore(directory);
            var first = new PackagedUiLanguagePlatform(display, windows);
            Launch(first, store);
            Assert.Equal(LanguageRecordRead.Of(LanguageProvenance.System(systemOverride)), store.Read());

            new World(localization: Real(first, store)).ViewModel.SelectedLanguageIndex = 2; // Português (Portugal)
            Assert.Equal(LanguageRecordRead.Of(LanguageProvenance.Explicit), store.Read());
            Assert.Equal("pt-PT", windows.Value);

            for (var relaunch = 0; relaunch < 2; relaunch++)
            {
                var process = new PackagedUiLanguagePlatform(display, windows); // a new process: the SDK value is empty
                var service = Launch(process, store);

                Assert.Equal(LanguageRecordRead.Of(LanguageProvenance.Explicit), store.Read());
                Assert.Empty(process.Writes);
                Assert.Equal("pt-PT", windows.Value);
                Assert.Equal("pt-PT", service.CurrentLanguageOverride);
                Assert.False(service.FollowsSystemLanguage);
                Assert.Equal("pt-PT", service.FormattingCultureName); // #57: numbers and dates in the chosen language
                Assert.Equal(2, new World(localization: service).ViewModel.SelectedLanguageIndex);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// D-2 / #55 D-B2 B (Cortex test 3): a v1.x pt-BR persisted by Windows, a new process (SDK value ""), an unsupported
    /// display - before the fix the hint could never appear in the installed app (the getter never returned the pt-BR).
    /// Now it does, and the launch leaves the legacy override and its absent record alone.
    /// </summary>
    [Fact]
    public void D2_TheLegacyPtBrHint_IsVisible_InANewProcessOfThePackagedApp()
    {
        var windows = new PackagedUiLanguagePlatform.PersistedOverride { Value = "pt-BR" };
        var store = new FakeLanguageSettingsStore();
        var process = new PackagedUiLanguagePlatform("fr-FR", windows);

        var service = Launch(process, store);

        Assert.Empty(process.Writes);
        Assert.Empty(store.Writes);
        Assert.Equal("pt-BR", windows.Value);
        Assert.Equal("pt-BR", service.CurrentLanguageOverride);
        Assert.Equal("en-US", service.PendingLegacyLanguageHint);
    }

    /// <summary>Control (Cortex test 4): unpackaged nothing is persisted - a new process starts from the system language.</summary>
    [Fact]
    public void D2_Unpackaged_ANewProcessReadsOnlyItsOwnValue()
    {
        var windows = new PackagedUiLanguagePlatform.PersistedOverride { Value = "pt-PT" }; // never read when unpackaged
        var store = new FakeLanguageSettingsStore();
        var process = new PackagedUiLanguagePlatform("fr-FR", windows) { Identity = PackageIdentity.Unpackaged };

        var service = Launch(process, store);

        Assert.Equal(["en-US"], process.Writes);                 // REL.0C: the system language's override, as today
        Assert.Equal("pt-PT", windows.Value);                    // and nothing reached Windows
        Assert.Equal("en-US", service.CurrentLanguageOverride);
    }

    /// <summary>
    /// PR #59 round 2 (Vigil L-1): when the persisted override cannot be read - or the package identity cannot be
    /// determined - the launch does not decide on "no override": an explicit choice keeps its record and its override,
    /// also on an unsupported Windows language where the system override would replace it.
    /// </summary>
    [Theory]
    [InlineData("pt-BR", "Packaged")]
    [InlineData("fr-FR", "Packaged")]
    [InlineData("en-GB", "Unknown")]
    public void D2_AnUnreadableOverride_IsNotNoOverride_TheLaunchWritesNothing(string display, string identity)
    {
        var windows = new PackagedUiLanguagePlatform.PersistedOverride { Value = "pt-PT" };
        var store = FakeLanguageSettingsStore.ExplicitChoice();
        var process = new PackagedUiLanguagePlatform(display, windows) { Identity = Enum.Parse<PackageIdentity>(identity), FailPersistedRead = true };

        var service = Launch(process, store);

        Assert.False(process.IsOverrideKnown);
        Assert.Empty(process.Writes);
        Assert.Empty(store.Writes);
        Assert.Equal("pt-PT", windows.Value);
        Assert.Equal(LanguageRecordRead.Of(LanguageProvenance.Explicit), store.Read());
        Assert.Null(service.CurrentLanguageOverride); // display fallback only: the process value of a new process
    }

    /// <summary>"Use system language" clears the persisted override too, so the next launch follows the system again.</summary>
    [Fact]
    public void D2_UseSystemLanguage_IsStillPersisted_AcrossRelaunches()
    {
        var windows = new PackagedUiLanguagePlatform.PersistedOverride { Value = "pt-PT" };
        var store = FakeLanguageSettingsStore.ExplicitChoice();
        var session = new PackagedUiLanguagePlatform("pt-BR", windows);

        new World(localization: Real(session, store)).ViewModel.SelectedLanguageIndex = 0;

        Assert.Equal(string.Empty, windows.Value);
        var next = Launch(new PackagedUiLanguagePlatform("pt-BR", windows), store);
        Assert.Null(next.CurrentLanguageOverride);
        Assert.True(next.FollowsSystemLanguage);
        Assert.Equal(0, new World(localization: next).ViewModel.SelectedLanguageIndex);
    }

    /// <summary>
    /// PR #58 × #59 merge: both fixes on the VM's machine at once - packaged semantics (the persisted override is read in
    /// a new process; "" clears in Windows) AND F-1 (display pt-BR, preferred [en-US, …]: the profile language en-US is
    /// the app's "system language"). An explicit pt-PT survives the relaunch with pt-PT text and numbers; "Use system
    /// language" then follows the PROFILE (en-US text and numbers) after the next relaunch. The widget follows the
    /// profile throughout - it never sees an in-app choice (ADR-018 §17, pre-existing).
    /// Through the real handler, the real store (a temp folder) and the real launch path.
    /// </summary>
    [Fact]
    public void Merge_PackagedApp_OnTheVmMachine_KeepsTheChoice_AndUseSystemFollowsTheProfile()
    {
        const string display = "pt-BR";
        string[] preferred = ["en-US", "pt-BR"];
        var profile = SupportedCultures.ProfileLanguage(preferred, display);
        Assert.Equal("en-US", profile);
        var directory = Path.Combine(Path.GetTempPath(), "sm-merge58-" + Guid.NewGuid().ToString("N"));
        try
        {
            var windows = new PackagedUiLanguagePlatform.PersistedOverride();
            var store = FileStore(directory);
            PackagedUiLanguagePlatform NewProcess() => new(profile, windows) { DisplayCultureName = display };
            string Text(LocalizationService s) => TestSupport.MrtRuntimeLanguages.ResourceLanguage(s.CurrentLanguageOverride, preferred);

            var first = Launch(NewProcess(), store);
            Assert.Equal(("en-US", "en-US"), (Text(first), first.FormattingCultureName)); // F-1 A: one language

            new World(localization: first).ViewModel.SelectedLanguageIndex = 2;           // Português (Portugal)
            var chosen = Launch(NewProcess(), store);                                       // relaunch (new process)
            Assert.Equal(LanguageRecordRead.Of(LanguageProvenance.Explicit), store.Read());
            Assert.Equal(("pt-PT", "pt-PT"), (Text(chosen), chosen.FormattingCultureName));
            Assert.Equal(2, new World(localization: chosen).ViewModel.SelectedLanguageIndex);
            Assert.Equal("en-US", SupportedCultures.Resolve(profile));                     // the widget's language

            new World(localization: chosen).ViewModel.SelectedLanguageIndex = 0;          // Use system language
            Assert.Equal(string.Empty, windows.Value);
            var system = Launch(NewProcess(), store);                                       // relaunch
            Assert.Null(system.CurrentLanguageOverride);
            Assert.True(system.FollowsSystemLanguage);
            Assert.Equal(("en-US", "en-US"), (Text(system), system.FormattingCultureName));
            Assert.Equal(0, new World(localization: system).ViewModel.SelectedLanguageIndex);
            Assert.Null(system.PendingLegacyLanguageHint);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// PR #59 round 2 (Cortex M-1, CONFIRMED on the VM): display pt-BR, Settings › Português (Portugal), then - same
    /// session - "Use system language". The SDK setter rejects "" (the fake throws like it), so before the hardening the
    /// record said System("") while Windows kept pt-PT and the next launch was still pt-PT. Now the persisted override is
    /// cleared in Windows: the next launch has no override, follows the system, and Settings shows "Use system language".
    /// Through the real handler, the real store (a temp folder) and the real launch path.
    /// </summary>
    [Fact]
    public void D2_UseSystemLanguage_AfterAnExplicitChoice_ClearsThePersistedOverride()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sm-d2c-" + Guid.NewGuid().ToString("N"));
        try
        {
            var windows = new PackagedUiLanguagePlatform.PersistedOverride();
            var store = FileStore(directory);
            var session = new PackagedUiLanguagePlatform("pt-BR", windows);
            var service = Launch(session, store);
            var settings = new World(localization: service).ViewModel;

            settings.SelectedLanguageIndex = 2;   // Português (Portugal)
            Assert.Equal("pt-PT", windows.Value);
            settings.SelectedLanguageIndex = 0;   // Use system language, same session

            Assert.True(settings.IsRestartNoticeOpen);
            Assert.Equal(0, settings.SelectedLanguageIndex);
            Assert.Equal(string.Empty, windows.Value);                       // cleared in Windows, not left at pt-PT
            Assert.Equal(LanguageRecordRead.Of(LanguageProvenance.System("")), store.Read());

            var next = Launch(new PackagedUiLanguagePlatform("pt-BR", windows), store);
            Assert.Null(next.CurrentLanguageOverride);
            Assert.True(next.FollowsSystemLanguage);
            Assert.Equal(0, new World(localization: next).ViewModel.SelectedLanguageIndex);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static LocalizationService RealLocalization(FakeUiLanguagePlatform platform, FakeLanguageSettingsStore store) =>
        new(NullLogger<LocalizationService>.Instance, platform, store);

    private static LocalizationService Real(IUiLanguagePlatform platform, ILanguageSettingsStore store) =>
        new(NullLogger<LocalizationService>.Instance, platform, store);

    private static JsonLanguageSettingsStore FileStore(string directory) =>
        new(new LanguageSettingsStorageOptions { FilePath = Path.Combine(directory, "language-settings.json"), Writer = "unpackaged" },
            NullLogger<JsonLanguageSettingsStore>.Instance);

    /// <summary>One launch through the real startup path (the process UI culture recorded, never changed).</summary>
    private static LocalizationService Launch(IUiLanguagePlatform platform, ILanguageSettingsStore store)
    {
        var service = Real(platform, store);
        using (App.BuildServicesHost(
                   services => services.AddSingleton<ILocalizationService>(service),
                   static _ => { }))
        {
        }

        return service;
    }

    // ---- idempotent, concurrency-safe load ------------------------------------------------------------------------

    /// <summary>Both sub-pages load on Loaded: overlapping loads share ONE run and the list is never duplicated.</summary>
    [Fact]
    public async Task OverlappingLoads_ShareOneRun_AndOneMorePass_NeverDuplicatingRows()
    {
        var world = new World();
        world.Servers.Servers.AddRange([Hidden("a"), Hidden("b"), Visible("c")]);
        var gate = new TaskCompletionSource();
        var calls = 0;
        world.Servers.GetAllOverride = async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await gate.Task;
            }

            return world.Servers.Servers.ToList();
        };

        var first = world.ViewModel.LoadAsync();   // General's Loaded
        var second = world.ViewModel.LoadAsync();  // Data's Loaded, while the first is in flight
        var third = world.ViewModel.LoadAsync();
        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Equal(1, Volatile.Read(ref calls));

        gate.SetResult();
        await first.WaitAsync(DeadlockGuard);

        Assert.Equal(2, Volatile.Read(ref calls)); // exactly one more pass for the calls made meanwhile
        Assert.Equal(new[] { "a", "b" }, world.ViewModel.HiddenServers.Select(item => item.Name));
        Assert.True(world.ViewModel.HasHiddenServers);
    }

    [Fact]
    public async Task AChangeDuringALoad_IsNeverLost_TheNewestDataWins()
    {
        var world = new World();
        world.Servers.Servers.Add(Hidden("a"));
        var gate = new TaskCompletionSource();
        var calls = 0;
        world.Servers.GetAllOverride = async _ =>
        {
            var snapshot = world.Servers.Servers.ToList();
            if (Interlocked.Increment(ref calls) == 1)
            {
                await gate.Task;
            }

            return snapshot;
        };

        var load = world.ViewModel.LoadAsync();
        world.Servers.Servers.Add(Hidden("b"));
        world.Servers.RaiseChanged(); // ServersChanged → LoadAsync while the first load is in flight
        gate.SetResult();
        await load.WaitAsync(DeadlockGuard);

        Assert.Equal(new[] { "a", "b" }, world.ViewModel.HiddenServers.Select(item => item.Name));
    }

    [Fact]
    public async Task SequentialLoads_AreIdempotent()
    {
        var world = new World();
        world.Servers.Servers.AddRange([Hidden("a"), Visible("c")]);

        await world.ViewModel.LoadAsync().WaitAsync(DeadlockGuard);
        await world.ViewModel.LoadAsync().WaitAsync(DeadlockGuard);

        Assert.Equal(new[] { "a" }, world.ViewModel.HiddenServers.Select(item => item.Name));
    }

    [Fact]
    public async Task AFailingLoad_ReportsIt_AndALaterLoadStillRuns()
    {
        var world = new World();
        world.Servers.GetAllOverride = _ => throw new IOException("synthetic");

        await world.ViewModel.LoadAsync().WaitAsync(DeadlockGuard);
        Assert.True(world.ViewModel.IsServerOperationErrorOpen);

        world.Servers.GetAllOverride = null;
        world.Servers.Servers.Add(Hidden("a"));
        await world.ViewModel.LoadAsync().WaitAsync(DeadlockGuard);
        Assert.Single(world.ViewModel.HiddenServers);
    }

    // ---- hidden-server restore ------------------------------------------------------------------------------------

    [Fact]
    public async Task RestoringAHiddenServer_RemovesItFromTheList()
    {
        var world = new World();
        world.Servers.Servers.AddRange([Hidden("a"), Hidden("b")]);
        world.Servers.RestoreOverride = id =>
        {
            var index = world.Servers.Servers.FindIndex(server => server.Id == id);
            world.Servers.Servers[index] = world.Servers.Servers[index] with { IsHidden = false };
            world.Servers.RaiseChanged();
            return Task.FromResult(true);
        };
        await world.ViewModel.LoadAsync().WaitAsync(DeadlockGuard);

        // Atlas C1 finding 3: from IDLE, the reload must come from ServersChanged (production), never from the test. The
        // reload's query is gated: it must have STARTED by the time the command finished, and the list is awaited through
        // its own change - no extra LoadAsync here.
        var reloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Servers.GetAllOverride = async _ =>
        {
            reloadStarted.TrySetResult();
            await release.Task;
            return world.Servers.Servers.ToList();
        };
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.ViewModel.HiddenServers.CollectionChanged += (_, _) =>
        {
            if (world.ViewModel.HiddenServers.Select(item => item.Name).SequenceEqual(["b"]))
            {
                restored.TrySetResult();
            }
        };

        await ((AsyncRelayCommand)world.ViewModel.HiddenServers[0].RestoreCommand).ExecuteAsync().WaitAsync(DeadlockGuard);
        Assert.True(reloadStarted.Task.IsCompleted, "the restore did not trigger the event-driven reload");
        release.SetResult();
        await restored.Task.WaitAsync(DeadlockGuard);

        Assert.Equal(new[] { "b" }, world.ViewModel.HiddenServers.Select(item => item.Name));
        Assert.Equal(new[] { 1 }, world.ViewModel.HiddenServers.Select(item => item.SizeOfSet));
        Assert.False(world.ViewModel.IsServerOperationErrorOpen);
        Assert.True(world.ViewModel.HasHiddenServers);
    }

    [Fact]
    public async Task ARestoreThatReturnsFalse_OpensTheError_AndKeepsTheRow()
    {
        var world = new World();
        world.Servers.Servers.Add(Hidden("a"));
        world.Servers.RestoreOverride = _ => Task.FromResult(false);
        await world.ViewModel.LoadAsync().WaitAsync(DeadlockGuard);

        world.ViewModel.HiddenServers[0].RestoreCommand.Execute(null);

        Assert.True(world.ViewModel.IsServerOperationErrorOpen);
        Assert.Single(world.ViewModel.HiddenServers);
    }

    [Fact]
    public async Task ARestoreRefusedByARestoreInProgress_SaysLocked_NotError()
    {
        var world = new World();
        world.Servers.Servers.Add(Hidden("a"));
        world.Servers.RestoreOverride = _ => throw new ConfigurationLockedException();
        await world.ViewModel.LoadAsync().WaitAsync(DeadlockGuard);

        world.ViewModel.HiddenServers[0].RestoreCommand.Execute(null);

        Assert.True(world.ViewModel.IsConfigurationLockedOpen);
        Assert.False(world.ViewModel.IsServerOperationErrorOpen);
    }

    // ---- reset ignored devices -----------------------------------------------------------------------------------

    [Fact]
    public void ResettingIgnoredDevices_SaysSuccess_OrError()
    {
        var world = new World();

        world.ViewModel.ResetIgnoredCommand.Execute(null);
        Assert.True(world.ViewModel.IsResetIgnoredSuccessOpen);
        Assert.False(world.ViewModel.IsResetIgnoredErrorOpen);
        Assert.Equal(1, world.Discovery.Resets);

        world.Discovery.Fail = true;
        world.ViewModel.ResetIgnoredCommand.Execute(null);
        Assert.False(world.ViewModel.IsResetIgnoredSuccessOpen);
        Assert.True(world.ViewModel.IsResetIgnoredErrorOpen);
    }

    // ---- clear / reset history through the (now UI-free) maintenance service --------------------------------------

    [Fact]
    public void ACancelledClear_DeletesNothing_AndOpensNothing()
    {
        var world = new World(maintenance: Maintenance(out var history, confirm: false));

        world.ViewModel.ClearHistoryCommand.Execute(null);

        Assert.Equal((1, 0, 0), (history.ClearConfirmations, history.Clears, history.Forgets));
        Assert.False(world.ViewModel.IsHistoryClearedOpen);
        Assert.False(world.ViewModel.IsHistoryClearErrorOpen);
    }

    [Fact]
    public void AConfirmedClear_ClearsAndForgetsTheCadence_AndSaysSo()
    {
        var world = new World(maintenance: Maintenance(out var history, confirm: true));

        world.ViewModel.ClearHistoryCommand.Execute(null);

        Assert.Equal((1, 1, 1), (history.ClearConfirmations, history.Clears, history.Forgets));
        Assert.True(world.ViewModel.IsHistoryClearedOpen);
        Assert.False(world.ViewModel.IsHistoryClearErrorOpen);
    }

    [Fact]
    public void AClearOverAnUnavailableStore_DeletesNothing_AndOffersTheReset()
    {
        var world = new World(maintenance: Maintenance(out var history, confirm: true, available: false));

        world.ViewModel.ClearHistoryCommand.Execute(null);

        Assert.Equal(0, history.Clears);
        Assert.True(world.ViewModel.IsHistoryClearErrorOpen);
        Assert.True(world.ViewModel.IsHistoryResetAvailable);
    }

    [Fact]
    public void AClearTheWriterCouldNotComplete_IsAnError_AndTheCadenceIsKept()
    {
        var world = new World(maintenance: Maintenance(out var history, confirm: true, clearSucceeds: false));

        world.ViewModel.ClearHistoryCommand.Execute(null);

        Assert.Equal((1, 0), (history.Clears, history.Forgets));
        Assert.True(world.ViewModel.IsHistoryClearErrorOpen);
    }

    [Theory]
    [InlineData(false, true, false, false)] // cancelled: nothing happens
    [InlineData(true, true, true, false)]   // reset: success, reset no longer offered
    [InlineData(true, false, false, true)]  // writer failed: error, reset still offered
    public void Reset_FollowsTheConfirmationAndTheWriter(bool confirm, bool resetSucceeds, bool successOpen, bool errorOpen)
    {
        var world = new World(maintenance: Maintenance(out var history, confirm, available: false, resetSucceeds: resetSucceeds));

        world.ViewModel.ResetHistoryCommand.Execute(null);

        Assert.Equal(1, history.ResetConfirmations);
        Assert.Equal(confirm ? 1 : 0, history.Resets);
        Assert.Equal(successOpen, world.ViewModel.IsHistoryResetOpen);
        Assert.Equal(errorOpen, world.ViewModel.IsHistoryResetErrorOpen);
        if (successOpen)
        {
            Assert.False(world.ViewModel.IsHistoryResetAvailable);
            Assert.Equal(1, history.Forgets);
        }
    }

    // ---- General ↔ Data navigation, About landing ------------------------------------------------------------------

    [Fact]
    public void TheInPageLinks_NavigateBetweenTheSubPages()
    {
        var world = new World();

        world.ViewModel.OpenDataCommand.Execute(null);
        world.ViewModel.OpenAboutCommand.Execute(null);
        world.ViewModel.BackToGeneralCommand.Execute(null);

        Assert.Equal(new[] { SettingsSection.Data, SettingsSection.About, SettingsSection.General }, world.Navigation.SettingsSections);
    }

    [Fact]
    public void TheAboutRequest_IsConsumedByTheDataPage_Once()
    {
        var world = new World();
        world.ViewModel.OpenAboutCommand.Execute(null);

        world.ViewModel.NotifyDataNavigatedTo();
        Assert.True(world.ViewModel.IsAboutSectionRequested);
        world.ViewModel.NotifyDataNavigatedTo();
        Assert.False(world.ViewModel.IsAboutSectionRequested);
    }

    [Fact]
    public void TheBackgroundRequest_IsConsumedByTheGeneralPage_Once()
    {
        var world = new World();
        world.Navigation.RequestBackgroundSettingsFocus();

        world.ViewModel.NotifyNavigatedTo();
        Assert.True(world.ViewModel.IsBackgroundSectionRequested);
        world.ViewModel.NotifyNavigatedTo();
        Assert.False(world.ViewModel.IsBackgroundSectionRequested);
    }

    /// <summary>Cortex §6.2: view models expose no WinUI <see cref="Visibility"/> (a bool + converter in the view).</summary>
    [Fact]
    public void ViewModels_ExposeNoVisibility()
    {
        var offenders = typeof(SettingsViewModel).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(SettingsViewModel).Namespace)
            .SelectMany(type => type.GetProperties().Select(property => (type, property)))
            .Where(pair => pair.property.PropertyType == typeof(Visibility))
            .Select(pair => $"{pair.type.Name}.{pair.property.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static Server Hidden(string name) => new()
    {
        Id = Guid.NewGuid(), Name = name, Host = $"{name}.local", Username = "qa", IsHidden = true,
        CreatedAt = Ui4TestKit.Now.AddMinutes(name[0])
    };

    private static Server Visible(string name) => Hidden(name) with { IsHidden = false };

    private static HistoryMaintenanceService Maintenance(
        out RecordingHistory history,
        bool confirm,
        bool available = true,
        bool clearSucceeds = true,
        bool resetSucceeds = true)
    {
        var recording = new RecordingHistory(confirm);
        history = recording;
        var store = new FakeServerHistoryStore { Available = available };
        return new HistoryMaintenanceService(
            store,
            () =>
            {
                recording.Clears++;
                return Task.FromResult(clearSucceeds);
            },
            () =>
            {
                recording.Resets++;
                return Task.FromResult(resetSucceeds);
            },
            () => recording.Forgets++,
            recording);
    }

    private sealed class World
    {
        public World(
            AppThemePreference theme = AppThemePreference.System,
            string? language = null,
            IHistoryMaintenanceService? maintenance = null,
            ILocalizationService? localization = null)
        {
            Theme = new RecordingTheme(theme);
            Localization = new RecordingLocalization(language);
            ViewModel = new SettingsViewModel(
                Theme,
                localization ?? Localization,
                Navigation,
                Servers,
                Discovery,
                new InertNotificationSettings(),
                new FakeBackgroundMonitoringSettingsService(enabled: true),
                new BackgroundDegradationNotice(),
                maintenance ?? new NullHistoryMaintenanceService(),
                new AppVersionProvider(),
                NullLogger<SettingsViewModel>.Instance,
                new PresentationClock(Clock)); // Atlas C2 finding 1: the toast countdown never uses the system clock
        }

        public TimerRecordingTimeProvider Clock { get; } = new();

        public SettingsViewModel ViewModel { get; }

        public RecordingTheme Theme { get; }

        public RecordingLocalization Localization { get; }

        public FakeNavigationService Navigation { get; } = new();

        public FakeServerService Servers { get; } = new();

        public RecordingDiscovery Discovery { get; } = new();
    }

    internal sealed class RecordingHistory(bool confirm) : IHistoryMaintenanceInteraction
    {
        public int ClearConfirmations { get; private set; }

        public int ResetConfirmations { get; private set; }

        public int Clears { get; set; }

        public int Resets { get; set; }

        public int Forgets { get; set; }

        public Task<bool> ConfirmClearHistoryAsync()
        {
            ClearConfirmations++;
            return Task.FromResult(confirm);
        }

        public Task<bool> ConfirmResetHistoryAsync()
        {
            ResetConfirmations++;
            return Task.FromResult(confirm);
        }
    }

    internal sealed class RecordingTheme(AppThemePreference current) : IThemeService
    {
        public List<AppThemePreference> Applied { get; } = [];

        public AppThemePreference Current { get; private set; } = current;

        public void Attach(FrameworkElement rootElement) => throw new NotSupportedException();

        public void Detach(FrameworkElement rootElement) => throw new NotSupportedException();

        public void Apply(AppThemePreference preference)
        {
            Applied.Add(preference);
            Current = preference;
        }
    }

    internal sealed class RecordingLocalization(string? current) : ILocalizationService
    {
        private readonly FakeLocalizationService _strings = new();

        public List<string?> Set { get; } = [];

        public string? CurrentLanguageOverride => current;

        public string GetString(string resourceKey) => _strings.GetString(resourceKey);

        public void InitializeFromSystem()
        {
        }

        public void SetLanguage(string? languageTag) => Set.Add(languageTag);
    }

    internal sealed class RecordingDiscovery : IServerDiscoveryService
    {
        public event EventHandler? DiscoveredChanged { add { } remove { } }

        public bool Fail { get; set; }

        public int Resets { get; private set; }

        public IReadOnlyList<DiscoveredService> GetDiscovered() => [];

        public Task IgnoreAsync(ServiceInstanceIdentity identity, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResetIgnoredAsync(CancellationToken cancellationToken = default)
        {
            Resets++;
            return Fail ? Task.FromException(new IOException("synthetic")) : Task.CompletedTask;
        }
    }

    private sealed class InertNotificationSettings : INotificationSettingsService
    {
        public event EventHandler? NotificationsEnabledChanged { add { } remove { } }

        public bool NotificationsEnabled => true;

        public void SetNotificationsEnabled(bool enabled)
        {
        }
    }
}
