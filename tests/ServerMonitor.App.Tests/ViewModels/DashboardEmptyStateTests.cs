using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// M14.5 B5 — the dashboard's empty state: "Import from SSH" is a normal add, the local-network search
/// indicator appears only while discovery is REALLY running and has found nothing, suggestions are listed
/// inside the empty state, and a dashboard with servers keeps its separate section exactly as before.
/// </summary>
public sealed class DashboardEmptyStateTests
{
    [Fact]
    public async Task EmptyDashboard_WithLiveDiscoveryAndNothingFound_ShowsTheSearch()
    {
        var discovery = new LiveDiscovery { IsSearching = true };
        using var dashboard = Dashboard(discovery: discovery);

        await dashboard.LoadAsync();

        Assert.False(dashboard.IsOperationErrorOpen);
        Assert.False(dashboard.HasVisibleServers);
        Assert.True(dashboard.IsSearchingLocalNetwork);
        Assert.False(dashboard.ShowEmptyStateDiscoveries);
        Assert.False(dashboard.ShowDiscoverySection);
    }

    [Fact]
    public async Task DiscoveryThatIsNotLive_NeverShowsASearch()
    {
        // The inert default and the QA stand-ins do not implement the activity contract at all.
        using var inert = Dashboard(discovery: new NullServerDiscoveryService());
        await inert.LoadAsync();
        Assert.False(inert.IsSearchingLocalNetwork);

        // A live service that is not running (disabled, failed to start, stopped) shows nothing either.
        using var stopped = Dashboard(discovery: new LiveDiscovery { IsSearching = false });
        await stopped.LoadAsync();
        Assert.False(stopped.IsSearchingLocalNetwork);
    }

    [Fact]
    public async Task SearchIndicator_FollowsDiscoveryStartingAndStopping()
    {
        var discovery = new LiveDiscovery { IsSearching = false };
        using var dashboard = Dashboard(discovery: discovery);
        await dashboard.LoadAsync();
        var raised = new List<string?>();
        dashboard.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        discovery.SetSearching(true);
        Assert.True(dashboard.IsSearchingLocalNetwork);
        Assert.Contains(nameof(DashboardViewModel.IsSearchingLocalNetwork), raised);

        raised.Clear();
        discovery.SetSearching(false);
        Assert.False(dashboard.IsSearchingLocalNetwork);
        Assert.Contains(nameof(DashboardViewModel.IsSearchingLocalNetwork), raised);
    }

    [Fact]
    public async Task FoundSuggestions_ReplaceTheSearchInsideTheEmptyState()
    {
        var discovery = new LiveDiscovery { IsSearching = true };
        using var dashboard = Dashboard(discovery: discovery);
        await dashboard.LoadAsync();
        var raised = new List<string?>();
        dashboard.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        discovery.Set(DiscoveredServerViewModelTests.Service("Mac Studio", "mac-studio.local", 22));

        Assert.False(dashboard.IsSearchingLocalNetwork);
        Assert.True(dashboard.ShowEmptyStateDiscoveries);
        Assert.False(dashboard.ShowDiscoverySection);
        Assert.Contains(nameof(DashboardViewModel.IsSearchingLocalNetwork), raised);
        Assert.Contains(nameof(DashboardViewModel.ShowEmptyStateDiscoveries), raised);
        var row = Assert.Single(dashboard.DiscoveredServers);
        Assert.Equal("Found on your local network: Mac Studio", row.LocalNetworkHeadline);

        // Gone again (ignored or expired): back to searching, never an empty list.
        discovery.Set();
        Assert.True(dashboard.IsSearchingLocalNetwork);
        Assert.False(dashboard.ShowEmptyStateDiscoveries);
    }

    // ---- REL.0B (UI9-FW-PROMPT): discovery starts only from the explicit action, through the real service ----

    [Fact]
    public async Task DashboardLoad_NeverStartsDiscovery_AndOffersTheExplicitSearch()
    {
        var browser = new FakeMdnsServiceBrowser();
        await using var service = RealDiscovery(browser);
        using var dashboard = Dashboard(discovery: service);

        await dashboard.LoadAsync();

        Assert.Equal(0, browser.StartCount);
        Assert.True(dashboard.CanStartLocalNetworkSearch);
        Assert.False(dashboard.IsSearchingLocalNetwork);
    }

    [Fact]
    public async Task SearchCommand_StartsTheRealServiceOnce_AndSwapsTheActionForTheIndicator()
    {
        var browser = new FakeMdnsServiceBrowser();
        await using var service = RealDiscovery(browser);
        using var dashboard = Dashboard(discovery: service);
        await dashboard.LoadAsync();
        var raised = new List<string?>();
        dashboard.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        var command = Assert.IsType<AsyncRelayCommand>(dashboard.StartLocalNetworkSearchCommand);
        await command.ExecuteAsync(null);
        await command.ExecuteAsync(null);

        Assert.Equal(1, browser.StartCount);
        Assert.True(service.IsSearching);
        Assert.False(dashboard.CanStartLocalNetworkSearch);
        Assert.True(dashboard.IsSearchingLocalNetwork);
        Assert.Contains(nameof(DashboardViewModel.CanStartLocalNetworkSearch), raised);
        Assert.Contains(nameof(DashboardViewModel.IsSearchingLocalNetwork), raised);
        Assert.False(dashboard.IsOperationErrorOpen);

        // Window to background: the search stops and the explicit action is offered again.
        await LocalNetworkSearchLifetime.StopForBackground(service, NullLogger.Instance);
        Assert.Equal(1, browser.StopCount);
        Assert.True(dashboard.CanStartLocalNetworkSearch);
        Assert.False(dashboard.IsSearchingLocalNetwork);
    }

    [Fact]
    public async Task ExplicitSearch_IsOfferedOnlyOnTheEmptyDashboardWithIdleLiveDiscovery()
    {
        using var inert = Dashboard(discovery: new NullServerDiscoveryService());
        await inert.LoadAsync();
        Assert.False(inert.CanStartLocalNetworkSearch);
        inert.StartLocalNetworkSearchCommand.Execute(null);
        Assert.False(inert.IsOperationErrorOpen);

        var live = new LiveDiscovery { IsSearching = false };
        using var empty = Dashboard(discovery: live);
        await empty.LoadAsync();
        Assert.True(empty.CanStartLocalNetworkSearch);

        // Found something: the suggestions replace the action.
        live.Set(DiscoveredServerViewModelTests.Service("Mac Studio", "mac-studio.local", 22));
        Assert.False(empty.CanStartLocalNetworkSearch);

        var servers = new FakeServerService();
        servers.Servers.Add(DashboardDiscoveryViewModelTests.ServerForTest("10.0.0.5", 22));
        using var withServers = Dashboard(servers: servers, discovery: new LiveDiscovery { IsSearching = false });
        await withServers.LoadAsync();
        Assert.False(withServers.CanStartLocalNetworkSearch);
    }

    /// <summary>
    /// Cortex C-1, through the REAL service (the fake that used to stand in here cleared before signalling, the
    /// opposite of the service, so it could not see the defect). The hide stop raises IsSearchingChanged
    /// synchronously and the dashboard rebuilds from GetDiscovered() right there; no DiscoveredChanged follows a stop.
    /// </summary>
    [Fact]
    public async Task RealStop_AfterSomethingWasFound_LeavesNoStaleSuggestionsAndOffersTheSearchAgain()
    {
        var browser = new FakeMdnsServiceBrowser();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-14T10:00:00Z"));
        await using var service = new ServerDiscoveryService(
            browser, new GatedIgnoredDeviceStore(), NullLogger<ServerDiscoveryService>.Instance, time, DiscoveryOptions.Default);
        using var dashboard = Dashboard(discovery: service);
        await dashboard.LoadAsync();
        await service.StartSearchAsync();

        browser.EmitFound(DiscoveryInputPolicy.TryCreateObservation(
            "Mac Studio", "_ssh._tcp", "local.", "mac-studio.local", 22,
            [System.Net.IPAddress.Parse("192.168.1.20")], "nic-a", time.GetUtcNow())!);
        await AdvanceUntilAsync(time, () => dashboard.ShowEmptyStateDiscoveries);
        Assert.Single(dashboard.DiscoveredServers);
        Assert.False(dashboard.CanStartLocalNetworkSearch);

        await LocalNetworkSearchLifetime.StopForBackground(service, NullLogger.Instance);

        Assert.False(service.IsSearching);
        Assert.Equal(DiscoverySearchEnd.Stopped, service.LastSearchEnd);
        Assert.Empty(dashboard.DiscoveredServers);
        Assert.False(dashboard.ShowEmptyStateDiscoveries);
        Assert.True(dashboard.CanStartLocalNetworkSearch);
        Assert.False(dashboard.ShowLocalNetworkNothingFound);
    }

    [Fact]
    public async Task EndStates_NothingFoundAndFailed_ShowTheirCaptionWithTheActionAgain()
    {
        var live = new LiveDiscovery { IsSearching = false, LastSearchEnd = DiscoverySearchEnd.NothingFound };
        using var dashboard = Dashboard(discovery: live);
        await dashboard.LoadAsync();
        Assert.True(dashboard.CanStartLocalNetworkSearch);
        Assert.True(dashboard.ShowLocalNetworkNothingFound);
        Assert.False(dashboard.ShowLocalNetworkSearchFailed);

        live.LastSearchEnd = DiscoverySearchEnd.Failed;
        live.SetSearching(false);
        Assert.True(dashboard.ShowLocalNetworkSearchFailed);
        Assert.False(dashboard.ShowLocalNetworkNothingFound);

        // While searching, neither caption nor the action.
        live.LastSearchEnd = DiscoverySearchEnd.None;
        live.SetSearching(true);
        Assert.False(dashboard.ShowLocalNetworkNothingFound);
        Assert.False(dashboard.ShowLocalNetworkSearchFailed);
        Assert.False(dashboard.CanStartLocalNetworkSearch);
    }

    [Fact]
    public async Task RealFailedStart_IsReportedAndTheSearchCanBeRetried()
    {
        var browser = new FakeMdnsServiceBrowser { StartException = new InvalidOperationException("synthetic") };
        await using var service = RealDiscovery(browser);
        using var dashboard = Dashboard(discovery: service);
        await dashboard.LoadAsync();

        await Assert.IsType<AsyncRelayCommand>(dashboard.StartLocalNetworkSearchCommand).ExecuteAsync(null);

        Assert.False(service.IsSearching);
        Assert.Equal(DiscoverySearchEnd.Failed, service.LastSearchEnd);
        Assert.True(dashboard.ShowLocalNetworkSearchFailed);
        Assert.True(dashboard.CanStartLocalNetworkSearch);

        browser.StartException = null;
        await Assert.IsType<AsyncRelayCommand>(dashboard.StartLocalNetworkSearchCommand).ExecuteAsync(null);
        Assert.True(service.IsSearching);
        Assert.False(dashboard.ShowLocalNetworkSearchFailed);
    }

    /// <summary>Advances fake time in small steps until the condition holds (the service's workers run on the pool).</summary>
    internal static async Task AdvanceUntilAsync(FakeTimeProvider time, Func<bool> condition, int maxSteps = 600)
    {
        for (var step = 0; step < maxSteps && !condition(); step++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5);
        }

        Assert.True(condition(), "condition not reached");
    }

    private static ServerDiscoveryService RealDiscovery(FakeMdnsServiceBrowser browser) =>
        new(browser, new GatedIgnoredDeviceStore(), NullLogger<ServerDiscoveryService>.Instance);

    [Fact]
    public async Task EmptyStateSuggestion_AddsThroughTheExistingPrefillPath()
    {
        var dialog = new RecordingDialogService();
        var discovery = new LiveDiscovery { IsSearching = true };
        using var dashboard = Dashboard(dialog: dialog, discovery: discovery);
        await dashboard.LoadAsync();
        discovery.Set(DiscoveredServerViewModelTests.Service("Mac Studio", "mac-studio.local", 2222));

        Assert.Single(dashboard.DiscoveredServers).AddCommand.Execute(null);

        Assert.Equal(1, dialog.DiscoveryCount);
        Assert.Equal(0, dialog.AddCount);
        Assert.Equal(0, dialog.SshImportCount);
        Assert.Equal("Mac Studio", dialog.LastPrefill!.Name);
        Assert.Equal("mac-studio.local", dialog.LastPrefill.Host);
        Assert.Equal(2222, dialog.LastPrefill.Port);
    }

    [Fact]
    public async Task DashboardWithServers_KeepsTheSeparateSectionAndShowsNoEmptyStateDiscovery()
    {
        var servers = new FakeServerService();
        servers.Servers.Add(DashboardDiscoveryViewModelTests.ServerForTest("10.0.0.5", 22));
        var discovery = new LiveDiscovery { IsSearching = true };
        using var dashboard = Dashboard(servers: servers, discovery: discovery);

        await dashboard.LoadAsync();

        Assert.False(dashboard.IsOperationErrorOpen);
        Assert.True(dashboard.HasVisibleServers);
        Assert.False(dashboard.IsSearchingLocalNetwork);
        Assert.False(dashboard.ShowDiscoverySection);

        discovery.Set(DiscoveredServerViewModelTests.Service("Mac Studio", "mac-studio.local", 22));

        Assert.True(dashboard.HasDiscoveredServers);
        Assert.True(dashboard.ShowDiscoverySection);
        Assert.False(dashboard.ShowEmptyStateDiscoveries);
        Assert.False(dashboard.IsSearchingLocalNetwork);
    }

    [Fact]
    public async Task ImportFromSsh_OpensTheImportEditorAndSavesThroughTheNormalAddPath()
    {
        var profiles = new RecordingProfileService();
        var dialog = new RecordingDialogService { SshImportResult = EditorResult() };
        using var dashboard = Dashboard(profiles: profiles, dialog: dialog);

        dashboard.ImportFromSshCommand.Execute(null);
        await profiles.Added.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, dialog.SshImportCount);
        Assert.Equal(0, dialog.AddCount);
        Assert.Equal(0, dialog.DiscoveryCount);
        Assert.Equal("web-01", Assert.Single(profiles.Profiles).Configuration.Name);
        Assert.False(dashboard.IsOperationErrorOpen);
    }

    [Fact]
    public void ImportFromSsh_Cancelled_PersistsNothing()
    {
        var profiles = new RecordingProfileService();
        var dialog = new RecordingDialogService { SshImportResult = null };
        using var dashboard = Dashboard(profiles: profiles, dialog: dialog);

        dashboard.ImportFromSshCommand.Execute(null);

        Assert.Equal(1, dialog.SshImportCount);
        Assert.Empty(profiles.Profiles);
        Assert.False(dashboard.IsOperationErrorOpen);
    }

    [Fact]
    public void AddServer_StillOpensThePlainEditor()
    {
        var dialog = new RecordingDialogService();
        using var dashboard = Dashboard(dialog: dialog);

        dashboard.AddServerCommand.Execute(null);

        Assert.Equal(1, dialog.AddCount);
        Assert.Equal(0, dialog.SshImportCount);
    }

    [Fact]
    public void Dispose_StopsListeningToDiscoveryActivity()
    {
        var discovery = new LiveDiscovery();
        var dashboard = Dashboard(discovery: discovery);
        Assert.Equal(1, discovery.ActivitySubscribers);

        dashboard.Dispose();

        Assert.Equal(0, discovery.ActivitySubscribers);
    }

    private static DashboardViewModel Dashboard(
        FakeServerService? servers = null,
        IServerProfileService? profiles = null,
        IServerDialogService? dialog = null,
        IServerDiscoveryService? discovery = null) =>
        new(
            servers ?? new FakeServerService(),
            profiles ?? new RecordingProfileService(),
            dialog ?? new RecordingDialogService(),
            new EditorScriptSession((IEditorScript)(dialog ?? new RecordingDialogService())),
            new FakeConnectionStateStore(),
            new FakeServerMetricsStore(),
            new ServerMonitoringStateStore(),
            new FakeMonitoringEngine(),
            discovery ?? new NullServerDiscoveryService(),
            new FakeNavigationService(),
            new FakeLocalizationService(),
            NullLogger<DashboardViewModel>.Instance);

    private static ServerEditorResult EditorResult() => new()
    {
        Profile = new ServerProfileInput
        {
            Configuration = new ServerInput
            {
                Name = "web-01",
                Host = "10.0.0.5",
                Port = 22,
                Username = "deploy",
                OperatingSystem = ServerOperatingSystem.Auto,
                AuthenticationMethod = AuthenticationMethod.SshKey,
                PrivateKeyPath = Path.Combine(Path.GetTempPath(), "id_import_test")
            },
            CredentialChange = CredentialChange.Clear
        }
    };

    /// <summary>A discovery service that, like the real one, also reports whether it is running.</summary>
    private sealed class LiveDiscovery : IServerDiscoveryService, IServerDiscoveryActivity
    {
        private IReadOnlyList<DiscoveredService> _discovered = [];
        private EventHandler? _isSearchingChanged;

        public bool IsSearching { get; set; }

        public DiscoverySearchEnd LastSearchEnd { get; set; }

        public int ActivitySubscribers => _isSearchingChanged?.GetInvocationList().Length ?? 0;

        public event EventHandler? DiscoveredChanged;

        public event EventHandler? IsSearchingChanged
        {
            add => _isSearchingChanged += value;
            remove => _isSearchingChanged -= value;
        }

        public void SetSearching(bool value)
        {
            IsSearching = value;
            _isSearchingChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Set(params DiscoveredService[] discovered)
        {
            _discovered = discovered;
            DiscoveredChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task StartSearchAsync(CancellationToken cancellationToken = default)
        {
            SetSearching(true);
            return Task.CompletedTask;
        }

        public Task StopSearchAsync(CancellationToken cancellationToken = default)
        {
            SetSearching(false);
            return Task.CompletedTask;
        }

        public IReadOnlyList<DiscoveredService> GetDiscovered() => _discovered;

        public Task IgnoreAsync(ServiceInstanceIdentity identity, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ResetIgnoredAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingProfileService : IServerProfileService
    {
        public List<ServerProfileInput> Profiles { get; } = [];

        public TaskCompletionSource Added { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ServerOperationResult> AddAsync(ServerProfileInput input, CancellationToken cancellationToken = default)
        {
            Profiles.Add(input);
            Added.TrySetResult();
            return Task.FromResult(new ServerOperationResult(
                DashboardDiscoveryViewModelTests.ServerForTest(input.Configuration.Host, input.Configuration.Port),
                new ServerValidationResult(Array.Empty<ServerValidationError>())));
        }

        public Task<ServerOperationResult> UpdateAsync(
            Server existingServer,
            ServerProfileInput input,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> RemoveAsync(Server server, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingDialogService : IEditorScript
    {
        public ServerEditorResult? SshImportResult { get; init; }

        public int AddCount { get; private set; }

        public int DiscoveryCount { get; private set; }

        public int SshImportCount { get; private set; }

        public ServerDiscoveryPrefill? LastPrefill { get; private set; }

        public Task<ServerEditorResult?> ShowEditorAsync(Server? server)
        {
            AddCount++;
            return Task.FromResult<ServerEditorResult?>(null);
        }

        public Task<ServerEditorResult?> ShowEditorForDiscoveryAsync(ServerDiscoveryPrefill prefill)
        {
            DiscoveryCount++;
            LastPrefill = prefill;
            return Task.FromResult<ServerEditorResult?>(null);
        }

        public Task<ServerEditorResult?> ShowEditorForSshImportAsync()
        {
            SshImportCount++;
            return Task.FromResult(SshImportResult);
        }

        public Task<bool> ConfirmRemoveAsync(Server server) => Task.FromResult(false);
    }
}
