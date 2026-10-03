using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Discovery;
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
        public World(AppThemePreference theme = AppThemePreference.System, string? language = null, IHistoryMaintenanceService? maintenance = null)
        {
            Theme = new RecordingTheme(theme);
            Localization = new RecordingLocalization(language);
            ViewModel = new SettingsViewModel(
                Theme,
                Localization,
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
