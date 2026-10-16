using System.Net;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Tests.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// REL.0D O-7 on the real <see cref="DashboardViewModel"/> over the REAL <see cref="ServerDiscoveryService"/> (fake
/// browser, no socket, fake time): both surfaces (Settings › Data and the overview's first-server state) end a search
/// whose only announcers the page would not list at 40 s with the "no new servers" copy, list a new server at once, offer
/// Stop while searching, and stop when the user leaves them. Nothing here starts a search except the user's command.
/// </summary>
public sealed class LocalNetworkSearchEndTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
    // Cortex L-2: "not before the bound" is the contract; the upper bound only guards a hang (+20 s / +60 s, CI headroom).
    private static TimeSpan ToleranceFor(DiscoverySearchEnd end) =>
        TimeSpan.FromSeconds(end == DiscoverySearchEnd.TimeLimit ? 60 : 20);

    [Fact]
    public async Task Settings_whose_only_announcer_is_already_configured_ends_at_40_seconds_with_no_new_servers()
    {
        var w = new World();
        w.Servers.Servers.Add(DashboardDiscoveryViewModelTests.ServerForTest("10.0.0.5", 22));
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        await dashboard.LoadAsync();
        w.Navigation.GoToSettings(SettingsSection.Data);
        await w.StartAsync();
        w.Browser.EmitFound(Observation("web-01", "web-01.local", "10.0.0.5", w.Time));
        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => w.Service.GetDiscovered().Count == 1);
        Assert.Empty(dashboard.DiscoveredServers);
        Assert.True(dashboard.IsLocalNetworkSearchRunning);

        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => !dashboard.IsLocalNetworkSearchRunning);

        w.AssertEndedAt(DiscoverySearchEnd.NothingFound, TimeSpan.FromSeconds(40));
        Assert.True(dashboard.LocalNetworkSearchEndedNothingFound);
        Assert.True(dashboard.StartLocalNetworkSearchCommand.CanExecute(null)); // a way to search again
        Assert.False(dashboard.StopLocalNetworkSearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task Settings_before_the_overview_loaded_ends_at_40_seconds_when_the_announcer_turns_out_configured()
    {
        // The announcement lands while the configured servers are still being read: it is not usable YET, and once the
        // read says "configured" it never becomes usable, so the search still ends at 40 s.
        var w = new World();
        var release = new TaskCompletionSource<IReadOnlyList<Server>>(TaskCreationOptions.RunContinuationsAsynchronously);
        w.Servers.GetAllOverride = _ => release.Task;
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;

        var press = w.StartAsync();
        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => w.Browser.StartCount == 1);
        w.Browser.EmitFound(Observation("Mac Studio", "mac-studio.local", "192.168.1.20", w.Time));
        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => w.Time.GetUtcNow() - Start >= TimeSpan.FromSeconds(5));
        release.SetResult([DashboardDiscoveryViewModelTests.ServerForTest("mac-studio.local", 22)]);
        await press.WaitAsync(TimeSpan.FromSeconds(30));

        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => !dashboard.IsLocalNetworkSearchRunning);

        w.AssertEndedAt(DiscoverySearchEnd.NothingFound, TimeSpan.FromSeconds(40));
        Assert.True(dashboard.LocalNetworkSearchEndedNothingFound);
    }

    [Fact]
    public async Task Settings_lists_a_new_server_at_once_and_keeps_searching_past_40_seconds()
    {
        var w = new World(new DiscoveryOptions { MaxSearchDuration = TimeSpan.FromMinutes(2) });
        w.Servers.Servers.Add(DashboardDiscoveryViewModelTests.ServerForTest("10.0.0.5", 22));
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        await dashboard.LoadAsync();
        await w.StartAsync();
        w.Browser.EmitFound(Observation("web-01", "web-01.local", "10.0.0.5", w.Time));
        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => w.Time.GetUtcNow() - Start >= TimeSpan.FromSeconds(20));

        w.Browser.EmitFound(Observation("NAS", "nas.local", "192.168.1.30", w.Time));
        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => dashboard.HasDiscoveredServers, maxSteps: 4);
        Assert.Equal("NAS", Assert.Single(dashboard.DiscoveredServers).DisplayName);

        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => w.Time.GetUtcNow() - Start >= TimeSpan.FromSeconds(60));
        Assert.True(dashboard.IsLocalNetworkSearchRunning);
        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => !dashboard.IsLocalNetworkSearchRunning, TimeSpan.FromSeconds(1), 600);
        w.AssertEndedAt(DiscoverySearchEnd.TimeLimit, TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task The_overview_first_server_state_ends_at_40_seconds_when_its_only_announcer_is_ignored()
    {
        var mac = Observation("Mac Studio", "mac-studio.local", "192.168.1.20", new FakeTimeProvider(Start));
        var w = new World(ignored: new IgnoredStore(mac.Identity.StableHash));
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        await dashboard.LoadAsync();
        w.Navigation.GoToDashboard();
        Assert.True(dashboard.ShowFirstServerState);
        await w.StartAsync();
        Assert.True(dashboard.IsSearchingLocalNetwork);
        Assert.True(dashboard.ShowOverviewStopLocalNetworkSearch);
        w.Browser.EmitFound(Observation("Mac Studio", "mac-studio.local", "192.168.1.20", w.Time));

        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => !dashboard.IsLocalNetworkSearchRunning);

        w.AssertEndedAt(DiscoverySearchEnd.NothingFound, TimeSpan.FromSeconds(40));
        Assert.True(dashboard.ShowLocalNetworkNothingFound);
        Assert.True(dashboard.CanStartLocalNetworkSearch);
        Assert.False(dashboard.ShowOverviewStopLocalNetworkSearch);
    }

    [Fact]
    public async Task The_overview_offers_stop_while_it_lists_results_and_stop_ends_the_search()
    {
        var w = new World();
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        await dashboard.LoadAsync();
        w.Navigation.GoToDashboard();
        Assert.False(dashboard.StopLocalNetworkSearchCommand.CanExecute(null));
        await w.StartAsync();
        w.Browser.EmitFound(Observation("NAS", "nas.local", "192.168.1.30", w.Time));
        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => dashboard.ShowEmptyStateDiscoveries);
        Assert.False(dashboard.IsSearchingLocalNetwork); // the searching row gives way to the list...
        Assert.True(dashboard.ShowOverviewStopLocalNetworkSearch); // ...but Stop stays
        Assert.True(dashboard.StopLocalNetworkSearchCommand.CanExecute(null));

        await Assert.IsType<AsyncRelayCommand>(dashboard.StopLocalNetworkSearchCommand).ExecuteAsync(null);
        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => !dashboard.IsLocalNetworkSearchRunning);

        w.AssertStopped();
        Assert.Empty(dashboard.DiscoveredServers);
        Assert.False(dashboard.ShowOverviewStopLocalNetworkSearch);
        Assert.False(dashboard.StopLocalNetworkSearchCommand.CanExecute(null));
        Assert.True(dashboard.CanStartLocalNetworkSearch);
        Assert.False(dashboard.ShowLocalNetworkNothingFound);
    }

    [Fact]
    public async Task Stop_in_settings_ends_the_search_and_offers_it_again()
    {
        var w = new World();
        w.Servers.Servers.Add(DashboardDiscoveryViewModelTests.ServerForTest("10.0.0.5", 22));
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        await dashboard.LoadAsync();
        w.Navigation.GoToSettings(SettingsSection.Data);
        var stopChanged = 0;
        dashboard.StopLocalNetworkSearchCommand.CanExecuteChanged += (_, _) => stopChanged++;
        await w.StartAsync();
        Assert.True(stopChanged > 0, "Stop would stay disabled while the search runs");
        Assert.True(dashboard.StopLocalNetworkSearchCommand.CanExecute(null));
        Assert.False(dashboard.ShowOverviewStopLocalNetworkSearch); // a user with servers: Settings only

        await Assert.IsType<AsyncRelayCommand>(dashboard.StopLocalNetworkSearchCommand).ExecuteAsync(null);
        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => !dashboard.IsLocalNetworkSearchRunning);

        w.AssertStopped();
        Assert.True(dashboard.StartLocalNetworkSearchCommand.CanExecute(null));
        Assert.False(dashboard.LocalNetworkSearchEndedNothingFound);
    }

    [Fact]
    public async Task Stop_asks_the_view_to_move_focus_once_the_search_has_ended()
    {
        var w = new World();
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        await dashboard.LoadAsync();
        w.Navigation.GoToDashboard();
        var requests = new List<(bool Running, bool CanStart, bool CanStartFromSettings)>();
        dashboard.LocalNetworkSearchStopFocusRequested += (_, _) => requests.Add(
            (dashboard.IsLocalNetworkSearchRunning, dashboard.CanStartLocalNetworkSearch, dashboard.CanStartLocalNetworkSearchFromSettings));
        await w.StartAsync();

        await Assert.IsType<AsyncRelayCommand>(dashboard.StopLocalNetworkSearchCommand).ExecuteAsync(null);

        // Raised once, AFTER the search ended: both focus targets (overview link, Settings button) are offered again.
        Assert.Equal(new[] { (false, true, true) }, requests);
    }

    [Fact]
    public async Task Only_the_explicit_stop_asks_for_focus()
    {
        var w = new World();
        w.Servers.Servers.Add(DashboardDiscoveryViewModelTests.ServerForTest("10.0.0.5", 22));
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        await dashboard.LoadAsync();
        var requests = 0;
        dashboard.LocalNetworkSearchStopFocusRequested += (_, _) => requests++;

        w.Navigation.GoToSettings(SettingsSection.Data);
        await w.StartAsync();
        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => !w.Service.IsSearching); // the automatic end
        await w.StartAsync();
        await LocalNetworkSearchLifetime.StopForBackground(w.Service, NullLogger.Instance); // the hide path
        await w.StartAsync();
        w.Navigation.GoToHistory(); // leaving the page
        // The stop flips IsSearching before it closes the browser (on another thread): wait for the counted stop too.
        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => !w.Service.IsSearching && w.Browser.StopCount == 3);

        Assert.Equal(3, w.Browser.StopCount);
        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData("History")]
    [InlineData("Servers")]
    [InlineData("SettingsGeneral")]
    [InlineData("OverviewWithServers")]
    public async Task Leaving_settings_data_stops_the_search(string destination)
    {
        var w = new World();
        w.Servers.Servers.Add(DashboardDiscoveryViewModelTests.ServerForTest("10.0.0.5", 22));
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        await dashboard.LoadAsync();
        w.Navigation.GoToSettings(SettingsSection.Data);
        await w.StartAsync();

        switch (destination)
        {
            case "History": w.Navigation.GoToHistory(); break;
            case "Servers": w.Navigation.GoToServers(); break;
            case "SettingsGeneral": w.Navigation.GoToSettings(SettingsSection.General); break;
            default: w.Navigation.GoToDashboard(); break;
        }

        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => !w.Service.IsSearching);
        w.AssertStopped();
        Assert.False(dashboard.IsLocalNetworkSearchRunning);
    }

    [Fact]
    public async Task Moving_between_the_pages_that_host_the_search_keeps_it()
    {
        var w = new World();
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        await dashboard.LoadAsync(); // no servers: the overview shows the first-server state with the search
        w.Navigation.GoToDashboard();
        await w.StartAsync();

        w.Navigation.GoToSettings(SettingsSection.Data);
        // "Add" on a suggestion opens the editor page: the search goes on behind it (never ended mid-add).
        w.Navigation.GoToServerEditor(null!);
        w.Navigation.GoToDashboard();
        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => w.Time.GetUtcNow() - Start >= TimeSpan.FromSeconds(5));

        Assert.True(w.Service.IsSearching);
        Assert.Equal(0, w.Browser.StopCount);

        // ...and leaving them both ends it.
        w.Navigation.GoToHistory();
        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => !w.Service.IsSearching);
        w.AssertStopped();
    }

    [Fact]
    public async Task Leaving_while_the_start_is_still_in_flight_leaves_no_listener()
    {
        var ignored = new GatedIgnoredDeviceStore(open: false);
        var w = new World(ignored: ignored);
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        await dashboard.LoadAsync();
        w.Navigation.GoToSettings(SettingsSection.Data);

        var press = w.StartAsync();
        await ignored.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        w.Navigation.GoToHistory(); // the stop queues behind the start that holds the lifecycle gate
        ignored.Release();
        await press.WaitAsync(TimeSpan.FromSeconds(30));
        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => !w.Service.IsSearching && w.Browser.StopCount == 1);

        w.AssertStopped();
    }

    [Fact]
    public async Task Startup_and_navigation_never_start_a_search()
    {
        var w = new World();
        await using var _ = w.Service;
        using var dashboard = w.Dashboard;
        await dashboard.LoadAsync();
        w.Navigation.GoToDashboard();
        w.Navigation.GoToSettings(SettingsSection.Data);
        w.Navigation.GoToHistory();
        w.Navigation.GoToDashboard();
        await DiscoveryUsableResultTests.AdvanceUntilAsync(w.Time, () => w.Time.GetUtcNow() - Start >= TimeSpan.FromSeconds(60));

        Assert.Equal(0, w.Browser.StartCount);
        Assert.Equal(0, w.Browser.FoundSubscriberCount);
        Assert.False(w.Service.IsSearching);
        Assert.Equal(DiscoverySearchEnd.None, w.Service.LastSearchEnd);
    }

    [Fact]
    public void Disposing_the_view_model_drops_its_navigation_subscription()
    {
        var w = new World();
        var before = w.Navigation.NavigatedSubscribers;
        w.Dashboard.Dispose();

        Assert.Equal(before - 1, w.Navigation.NavigatedSubscribers);
    }

    // ------------------------------------------------------------------ view wiring + copy

    [Fact]
    public void Both_surfaces_bind_stop_to_the_one_command()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string page, string name) => Assert.Single(
            XDocument.Load(Path.Combine(RepositoryRoot(), "src", "ServerMonitor.App", "Views", page)).Descendants(),
            e => (string?)e.Attribute(x + "Name") == name);

        var overview = Named("DashboardPage.xaml", "EmptyDiscoveryStopButton");
        Assert.Equal("{x:Bind ViewModel.StopLocalNetworkSearchCommand}", (string?)overview.Attribute("Command"));
        Assert.Equal("{x:Bind ViewModel.ShowOverviewStopLocalNetworkSearch, Mode=OneWay}", (string?)overview.Attribute("Visibility"));
        Assert.Equal("DashboardEmptyDiscoveryStopButton", (string?)overview.Attribute(x + "Uid"));
        Assert.Contains(overview.Ancestors(), e => (string?)e.Attribute(x + "Name") == "FirstServerStateBlock");

        var settings = Named("SettingsDataPage.xaml", "LocalNetworkStopButton");
        Assert.Equal("{x:Bind Discovery.StopLocalNetworkSearchCommand}", (string?)settings.Attribute("Command"));
        Assert.Equal("SettingsLocalNetworkStopButton", (string?)settings.Attribute(x + "Uid"));
        Assert.Contains(settings.Ancestors(), e => (string?)e.Attribute(x + "Name") == "LocalNetworkSearchingRow");
    }

    /// <summary>
    /// Prism M-1: each page hands focus to ITS start control when Stop asks (Low priority, after the state change), and
    /// only while it is shown. Code-behind cannot run without a XAML runtime, so the wiring is pinned at the source.
    /// </summary>
    [Fact]
    public void Each_page_moves_focus_from_stop_to_its_start_control()
    {
        var settings = ReadView("SettingsDataPage.xaml.cs");
        Assert.Contains("Discovery.LocalNetworkSearchStopFocusRequested += OnLocalNetworkStopFocusRequested;", settings, StringComparison.Ordinal);
        Assert.Contains("Discovery.LocalNetworkSearchStopFocusRequested -= OnLocalNetworkStopFocusRequested;", settings, StringComparison.Ordinal);
        var settingsHandler = Body(settings, "private void OnLocalNetworkStopFocusRequested(");
        Assert.Contains("DispatcherQueuePriority.Low", settingsHandler, StringComparison.Ordinal);
        Assert.Contains("LocalNetworkSearchButton.Focus(", settingsHandler, StringComparison.Ordinal);
        // Subscribed while shown only: added in OnLoaded, removed in OnUnloaded.
        Assert.Contains("LocalNetworkSearchStopFocusRequested +=", Body(settings, "private async void OnLoaded("), StringComparison.Ordinal);
        Assert.Contains("LocalNetworkSearchStopFocusRequested -=", Body(settings, "private void OnUnloaded("), StringComparison.Ordinal);

        var overview = ReadView("DashboardPage.xaml.cs");
        Assert.Contains("ViewModel.LocalNetworkSearchStopFocusRequested += OnLocalNetworkStopFocusRequested;", overview, StringComparison.Ordinal);
        var overviewHandler = Body(overview, "private void OnLocalNetworkStopFocusRequested(");
        Assert.Contains("if (!IsLoaded)", overviewHandler, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.Low", overviewHandler, StringComparison.Ordinal);
        Assert.Contains("EmptyDiscoveryStartButton.Focus(", overviewHandler, StringComparison.Ordinal);
    }

    /// <summary>Prism L-2: Settings' searching row wraps its text; Stop keeps its own Auto column (never clipped first).</summary>
    [Fact]
    public void The_settings_searching_row_wraps_and_keeps_stop_in_its_own_column()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var xaml = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "ServerMonitor.App", "Views", "SettingsDataPage.xaml"));
        XElement Named(string name) => Assert.Single(xaml.Descendants(), e => (string?)e.Attribute(x + "Name") == name);

        var row = Named("LocalNetworkSearchingRow");
        Assert.Equal("Grid", row.Name.LocalName);
        Assert.Equal(
            new string?[] { "Auto", "*", "Auto" },
            row.Descendants().Where(e => e.Name.LocalName == "ColumnDefinition").Select(e => (string?)e.Attribute("Width")));
        Assert.Equal("1", (string?)Named("LocalNetworkSearchingText").Attribute("Grid.Column"));
        Assert.Equal("Wrap", (string?)Named("LocalNetworkSearchingText").Attribute("TextWrapping"));
        Assert.Equal("2", (string?)Named("LocalNetworkStopButton").Attribute("Grid.Column"));
    }

    [Theory]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public void Every_language_says_no_NEW_servers_and_names_stop(string language)
    {
        var resw = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "ServerMonitor.App", "Resources", language, "Resources.resw"));
        var values = resw.Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);
        const string automationName = "[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";

        var nothing = values["DashboardEmptyDiscoveryNothingFound.Text"];
        Assert.Contains(language == "en-US" ? "new" : "nov", nothing, StringComparison.OrdinalIgnoreCase);
        foreach (var key in new[] { "DashboardEmptyDiscoveryStopButton", "SettingsLocalNetworkStopButton" })
        {
            Assert.False(string.IsNullOrWhiteSpace(values[key + ".Content"]));
            // Prism L-1 (WCAG 2.5.3 Label in Name): the accessible name contains the visible label as whole words ("Stop
            // searching" does not contain the label "Stop search": a voice command for the label would not match it).
            Assert.Matches(
                @"(?<!\w)" + System.Text.RegularExpressions.Regex.Escape(values[key + ".Content"]) + @"(?!\w)",
                values[key + "." + automationName]);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static DiscoveryObservation Observation(string name, string host, string address, TimeProvider time) =>
        DiscoveryInputPolicy.TryCreateObservation(
            name, "_ssh._tcp", "local.", host, 22, [IPAddress.Parse(address)], "nic-a", time.GetUtcNow())!;

    private static string ReadView(string file) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "ServerMonitor.App", "Views", file));

    /// <summary>The text of the member that starts at <paramref name="signature"/>, up to the next member.</summary>
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found");
        var next = code.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? code[start..] : code[start..next];
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class World
    {
        private DateTimeOffset? _endedAt;

        public World(DiscoveryOptions? options = null, IIgnoredDeviceStore? ignored = null)
        {
            Service = new ServerDiscoveryService(
                Browser, ignored ?? new GatedIgnoredDeviceStore(), NullLogger<ServerDiscoveryService>.Instance, Time,
                options ?? DiscoveryOptions.Default);
            Service.IsSearchingChanged += (_, _) =>
            {
                if (!Service.IsSearching)
                {
                    _endedAt ??= Time.GetUtcNow();
                }
            };
            var script = new NoEditor();
            Dashboard = new DashboardViewModel(
                Servers,
                new NoProfiles(),
                script,
                new EditorScriptSession(script),
                new FakeConnectionStateStore(),
                new FakeServerMetricsStore(),
                new ServerMonitoringStateStore(),
                new FakeMonitoringEngine(),
                Service,
                Navigation,
                new FakeLocalizationService(),
                NullLogger<DashboardViewModel>.Instance);
        }

        public FakeServerService Servers { get; } = new();

        public FakeNavigationService Navigation { get; } = new();

        public FakeMdnsServiceBrowser Browser { get; } = new();

        public FakeTimeProvider Time { get; } = new(Start);

        public ServerDiscoveryService Service { get; }

        public DashboardViewModel Dashboard { get; }

        public Task StartAsync() => Assert.IsType<AsyncRelayCommand>(Dashboard.StartLocalNetworkSearchCommand).ExecuteAsync(null);

        public void AssertEndedAt(DiscoverySearchEnd end, TimeSpan bound)
        {
            Assert.Equal(end, Service.LastSearchEnd);
            Assert.NotNull(_endedAt);
            Assert.InRange(_endedAt.Value - Start, bound, bound + ToleranceFor(end));
            AssertNoListener();
        }

        public void AssertStopped()
        {
            Assert.Equal(DiscoverySearchEnd.Stopped, Service.LastSearchEnd);
            AssertNoListener();
        }

        private void AssertNoListener()
        {
            // A stop that ends on another thread (the automatic end, leaving a page) flips IsSearching before it
            // unsubscribes and closes the browser. Bounded wait for that close; the asserts below still decide.
            SpinWait.SpinUntil(() => Browser.StopCount >= Browser.StartCount, TimeSpan.FromSeconds(30));
            Assert.False(Service.IsSearching);
            Assert.Equal(1, Browser.StartCount);
            Assert.Equal(Browser.StartCount, Browser.StopCount);
            Assert.Equal(0, Browser.FoundSubscriberCount);
        }
    }

    private sealed class IgnoredStore(params string[] hashes) : IIgnoredDeviceStore
    {
        public Task<IReadOnlySet<string>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(hashes, StringComparer.Ordinal));

        public Task<bool> IgnoreAsync(string identityHash, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoEditor : IEditorScript
    {
        public Task<ServerEditorResult?> ShowEditorAsync(Server? server) => Task.FromResult<ServerEditorResult?>(null);

        public Task<ServerEditorResult?> ShowEditorForDiscoveryAsync(ServerDiscoveryPrefill prefill) =>
            Task.FromResult<ServerEditorResult?>(null);

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
