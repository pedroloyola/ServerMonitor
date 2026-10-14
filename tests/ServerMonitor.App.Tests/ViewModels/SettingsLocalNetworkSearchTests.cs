using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// REL.0D: Settings › Data "Procurar na rede local" - the explicit entry for a user who already has servers (the empty
/// overview's link is gone for them). The page binds the overview's ONE command and suggestion list, so these tests drive
/// that command on the real <see cref="DashboardViewModel"/> over the REAL <see cref="ServerDiscoveryService"/> and a
/// fake browser (no socket): one start, the same bounds and end states, the same stop on hide, the same Add/Ignore.
/// </summary>
public sealed class SettingsLocalNetworkSearchTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-14T12:00:00Z");

    [Fact]
    public async Task A_user_with_servers_searches_from_settings_through_the_one_command()
    {
        var w = new World();
        w.Servers.Servers.Add(DashboardDiscoveryViewModelTests.ServerForTest("10.0.0.5", 22));
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        await dashboard.LoadAsync();

        // The empty overview's link is not offered to this user; the Settings entry is.
        Assert.True(dashboard.HasVisibleServers);
        Assert.False(dashboard.CanStartLocalNetworkSearch);
        Assert.True(dashboard.IsLocalNetworkSearchAvailable);
        Assert.True(dashboard.CanStartLocalNetworkSearchFromSettings);
        Assert.True(dashboard.StartLocalNetworkSearchCommand.CanExecute(null));
        Assert.Equal(0, w.Browser.StartCount);

        await w.StartAsync();

        Assert.Equal(1, w.Browser.StartCount);
        Assert.True(dashboard.IsLocalNetworkSearchRunning);
        Assert.False(dashboard.CanStartLocalNetworkSearchFromSettings);
        Assert.False(dashboard.StartLocalNetworkSearchCommand.CanExecute(null));

        // A second press while searching does not open a second listener.
        await w.StartAsync();
        Assert.Equal(1, w.Browser.StartCount);

        // Suggestions are listed for this user (the configured 10.0.0.5:22 is not suggested again).
        w.Browser.EmitFound(Observation("web-01", "web-01.local", "10.0.0.5", w.Time));
        w.Browser.EmitFound(Observation("Mac Studio", "mac-studio.local", "192.168.1.20", w.Time));
        await DashboardEmptyStateTests.AdvanceUntilAsync(w.Time, () => dashboard.HasDiscoveredServers);
        Assert.Equal("Mac Studio", Assert.Single(dashboard.DiscoveredServers).DisplayName);
        Assert.True(dashboard.IsLocalNetworkSearchRunning);
    }

    [Fact]
    public async Task Settings_before_the_overview_ever_loaded_still_hides_servers_already_added()
    {
        // A --background launch opened on Settings from the tray: the overview never ran LoadAsync.
        var w = new World();
        w.Servers.Servers.Add(DashboardDiscoveryViewModelTests.ServerForTest("mac-studio.local", 22));
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;

        await w.StartAsync();
        w.Browser.EmitFound(Observation("Mac Studio", "mac-studio.local", "192.168.1.20", w.Time));
        w.Browser.EmitFound(Observation("NAS", "nas.local", "192.168.1.30", w.Time));
        await DashboardEmptyStateTests.AdvanceUntilAsync(w.Time, () => dashboard.HasDiscoveredServers);
        await DashboardEmptyStateTests.AdvanceUntilAsync(w.Time, () => w.Service.GetDiscovered().Count == 2);

        Assert.Equal("NAS", Assert.Single(dashboard.DiscoveredServers).DisplayName);
    }

    [Fact]
    public async Task A_failed_server_read_does_not_block_the_search_the_user_asked_for()
    {
        var w = new World();
        w.Servers.GetAllOverride = _ => throw new IOException("synthetic");
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;

        await w.StartAsync();

        Assert.Equal(1, w.Browser.StartCount);
        Assert.True(dashboard.IsLocalNetworkSearchRunning);
        Assert.False(dashboard.IsOperationErrorOpen);
    }

    [Fact]
    public async Task Nothing_found_after_40_seconds_is_shown_in_settings_and_the_search_is_offered_again()
    {
        var w = new World();
        w.Servers.Servers.Add(DashboardDiscoveryViewModelTests.ServerForTest("10.0.0.5", 22));
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        await dashboard.LoadAsync();
        await w.StartAsync();

        await DashboardEmptyStateTests.AdvanceUntilAsync(w.Time, () => !dashboard.IsLocalNetworkSearchRunning);

        Assert.Equal(DiscoverySearchEnd.NothingFound, w.Service.LastSearchEnd);
        Assert.True(dashboard.LocalNetworkSearchEndedNothingFound);
        Assert.False(dashboard.LocalNetworkSearchEndedTimeLimit);
        Assert.False(dashboard.LocalNetworkSearchEndedFailed);
        Assert.False(dashboard.ShowLocalNetworkNothingFound); // the overview caption stays an empty-state caption
        Assert.True(dashboard.StartLocalNetworkSearchCommand.CanExecute(null));
        Assert.Equal(1, w.Browser.StopCount);
        Assert.Equal(0, w.Browser.FoundSubscriberCount);
    }

    [Fact]
    public async Task The_time_limit_ends_a_search_started_from_settings()
    {
        var w = new World(new DiscoveryOptions { MaxSearchDuration = TimeSpan.FromMinutes(1) });
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        await w.StartAsync();
        w.Browser.EmitFound(Observation("Mac Studio", "mac-studio.local", "192.168.1.20", w.Time));

        await DashboardEmptyStateTests.AdvanceUntilAsync(w.Time, () => !dashboard.IsLocalNetworkSearchRunning, maxSteps: 1200);

        Assert.True(dashboard.LocalNetworkSearchEndedTimeLimit);
        Assert.False(dashboard.LocalNetworkSearchEndedNothingFound);
        Assert.Empty(dashboard.DiscoveredServers);
        Assert.False(dashboard.HasDiscoveredServers);
    }

    [Fact]
    public async Task A_failed_start_is_shown_in_settings_and_can_be_retried()
    {
        var w = new World();
        w.Browser.StartException = new InvalidOperationException("synthetic");
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;

        await w.StartAsync();

        Assert.True(dashboard.LocalNetworkSearchEndedFailed);
        Assert.False(dashboard.IsLocalNetworkSearchRunning);
        Assert.True(dashboard.StartLocalNetworkSearchCommand.CanExecute(null));

        w.Browser.StartException = null;
        await w.StartAsync();
        Assert.True(dashboard.IsLocalNetworkSearchRunning);
        Assert.False(dashboard.LocalNetworkSearchEndedFailed);
    }

    [Fact]
    public async Task Hiding_the_window_stops_a_search_started_from_settings()
    {
        var w = new World();
        w.Servers.Servers.Add(DashboardDiscoveryViewModelTests.ServerForTest("10.0.0.5", 22));
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        await dashboard.LoadAsync();
        await w.StartAsync();
        w.Browser.EmitFound(Observation("Mac Studio", "mac-studio.local", "192.168.1.20", w.Time));
        await DashboardEmptyStateTests.AdvanceUntilAsync(w.Time, () => dashboard.HasDiscoveredServers);

        // The same hide path the window controller runs on close-to-tray and minimize-to-tray.
        await LocalNetworkSearchLifetime.StopForBackground(w.Service, NullLogger.Instance);

        Assert.False(dashboard.IsLocalNetworkSearchRunning);
        Assert.Equal(1, w.Browser.StopCount);
        Assert.Equal(0, w.Browser.FoundSubscriberCount);
        Assert.Empty(dashboard.DiscoveredServers);
        Assert.False(dashboard.LocalNetworkSearchEndedNothingFound);
        Assert.True(dashboard.StartLocalNetworkSearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_suggestion_found_from_settings_adds_through_the_prefilled_editor_and_ignores_through_the_service()
    {
        var script = new Script();
        var ignored = new GatedIgnoredDeviceStore();
        var w = new World(script: script, ignored: ignored);
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        await w.StartAsync();
        w.Browser.EmitFound(Observation("Mac Studio", "mac-studio.local", "192.168.1.20", w.Time, port: 2222));
        await DashboardEmptyStateTests.AdvanceUntilAsync(w.Time, () => dashboard.HasDiscoveredServers);
        var row = Assert.Single(dashboard.DiscoveredServers);

        await Assert.IsType<AsyncRelayCommand>(row.AddCommand).ExecuteAsync(null);

        Assert.Equal("Mac Studio", script.LastPrefill!.Name);
        Assert.Equal("mac-studio.local", script.LastPrefill.Host);
        Assert.Equal(2222, script.LastPrefill.Port);

        await Assert.IsType<AsyncRelayCommand>(row.IgnoreCommand).ExecuteAsync(null);
        await DashboardEmptyStateTests.AdvanceUntilAsync(w.Time, () => !dashboard.HasDiscoveredServers);
        Assert.Empty(w.Service.GetDiscovered());
    }

    [Fact]
    public async Task The_settings_entry_is_told_when_a_search_starts_and_ends()
    {
        var w = new World();
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        var raised = new List<string?>();
        var canExecuteChanged = 0;
        dashboard.PropertyChanged += (_, args) => raised.Add(args.PropertyName);
        dashboard.StartLocalNetworkSearchCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

        await w.StartAsync();
        Assert.Contains(nameof(DashboardViewModel.IsLocalNetworkSearchRunning), raised);
        Assert.Contains(nameof(DashboardViewModel.CanStartLocalNetworkSearchFromSettings), raised);

        raised.Clear();
        canExecuteChanged = 0;
        await LocalNetworkSearchLifetime.StopForBackground(w.Service, NullLogger.Instance);
        Assert.Contains(nameof(DashboardViewModel.IsLocalNetworkSearchRunning), raised);
        Assert.Contains(nameof(DashboardViewModel.CanStartLocalNetworkSearchFromSettings), raised);
        Assert.Contains(nameof(DashboardViewModel.LocalNetworkSearchEndedNothingFound), raised);
        Assert.Contains(nameof(DashboardViewModel.LocalNetworkSearchEndedTimeLimit), raised);
        Assert.Contains(nameof(DashboardViewModel.LocalNetworkSearchEndedFailed), raised);
        Assert.True(canExecuteChanged > 0, "the Settings button would stay disabled after the search ended");
    }

    [Fact]
    public void Without_live_discovery_the_entry_is_hidden_and_offers_nothing()
    {
        using var dashboard = new World(discovery: new NullServerDiscoveryService()).Dashboard;

        Assert.False(dashboard.IsLocalNetworkSearchAvailable);
        Assert.False(dashboard.CanStartLocalNetworkSearchFromSettings);
        Assert.False(dashboard.IsLocalNetworkSearchRunning);
        Assert.False(dashboard.StartLocalNetworkSearchCommand.CanExecute(null));
    }

    private static DiscoveryObservation Observation(
        string name, string host, string address, FakeTimeProvider time, int port = 22) =>
        DiscoveryInputPolicy.TryCreateObservation(
            name, "_ssh._tcp", "local.", host, port, [IPAddress.Parse(address)], "nic-a", time.GetUtcNow())!;

    private sealed class World
    {
        public World(
            DiscoveryOptions? options = null,
            IEditorScript? script = null,
            GatedIgnoredDeviceStore? ignored = null,
            IServerDiscoveryService? discovery = null)
        {
            Browser = new FakeMdnsServiceBrowser();
            Time = new FakeTimeProvider(Start);
            Service = new ServerDiscoveryService(
                Browser, ignored ?? new GatedIgnoredDeviceStore(), NullLogger<ServerDiscoveryService>.Instance, Time,
                options ?? DiscoveryOptions.Default);
            script ??= new Script();
            Dashboard = new DashboardViewModel(
                Servers,
                new NoProfiles(),
                script,
                new EditorScriptSession(script),
                new FakeConnectionStateStore(),
                new FakeServerMetricsStore(),
                new ServerMonitoringStateStore(),
                new FakeMonitoringEngine(),
                discovery ?? Service,
                new FakeNavigationService(),
                new FakeLocalizationService(),
                NullLogger<DashboardViewModel>.Instance);
        }

        public FakeServerService Servers { get; } = new();

        public FakeMdnsServiceBrowser Browser { get; }

        public FakeTimeProvider Time { get; }

        public ServerDiscoveryService Service { get; }

        public DashboardViewModel Dashboard { get; }

        /// <summary>The Settings button's press: the overview's command, awaited.</summary>
        public Task StartAsync() => Assert.IsType<AsyncRelayCommand>(Dashboard.StartLocalNetworkSearchCommand).ExecuteAsync(null);
    }

    private sealed class Script : IEditorScript
    {
        public ServerDiscoveryPrefill? LastPrefill { get; private set; }

        public Task<ServerEditorResult?> ShowEditorAsync(Server? server) => Task.FromResult<ServerEditorResult?>(null);

        public Task<ServerEditorResult?> ShowEditorForDiscoveryAsync(ServerDiscoveryPrefill prefill)
        {
            LastPrefill = prefill;
            return Task.FromResult<ServerEditorResult?>(null);
        }

        public Task<ServerEditorResult?> ShowEditorForSshImportAsync() => Task.FromResult<ServerEditorResult?>(null);

        public Task<bool> ConfirmRemoveAsync(Server server) => Task.FromResult(false);
    }

    private sealed class NoProfiles : IServerProfileService
    {
        public Task<ServerOperationResult> AddAsync(ServerProfileInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ServerOperationResult> UpdateAsync(
            Server existingServer, ServerProfileInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(Server server, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
