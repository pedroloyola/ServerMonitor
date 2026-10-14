using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Tests.TestSupport;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// REL.0B (UI9-FW-PROMPT). Starting the mDNS browser binds UDP listeners on 0.0.0.0:5353 / [::]:5353 (Tmds.MDns
/// 0.9.1, statically proven in .boss/tmp/rel0/relay-b2.md), and that bind is what made Windows Firewall prompt ~2 s
/// after every launch - including <c>--background</c>, where there is no window at all. These tests pin the fix:
/// the host never starts discovery, the explicit empty-state action starts it exactly once, and hiding the window
/// stops it. Every browser here is a fake: nothing opens a socket.
/// </summary>
public sealed class DiscoveryOnDemandTests
{
    // ------------------------------------------------------------------ host start (Normal AND Background)

    /// <summary>
    /// The real composition, isolated. <c>App.OnLaunched</c> calls the same <c>ServicesHost.StartAsync()</c> for a
    /// Normal and a <c>--background</c> launch (the mode only decides whether a window is created), so the hosted
    /// participants below are exactly what both modes start.
    /// </summary>
    [Fact]
    public async Task Host_start_in_the_real_composition_never_starts_the_mdns_browser()
    {
        using var composition = new IsolatedAppComposition();
        var browser = new FakeMdnsServiceBrowser();
        composition.Services.AddSingleton<IMdnsServiceBrowser>(browser);
        await using var provider = composition.BuildProvider();

        var discoveryHosted = DiscoveryHostedServices(composition.Services, provider);

        // Still exactly one participant: the host must keep STOPPING discovery on exit, in the documented order.
        var hosted = Assert.Single(discoveryHosted);
        await hosted.StartAsync(CancellationToken.None);

        Assert.Equal(0, browser.StartCount);
        Assert.Equal(0, browser.FoundSubscriberCount);
        var activity = Assert.IsAssignableFrom<IServerDiscoveryActivity>(provider.GetRequiredService<IServerDiscoveryService>());
        Assert.False(activity.IsSearching);

        await hosted.StopAsync(CancellationToken.None);
        Assert.Equal(0, browser.StartCount);
    }

    [Fact]
    public async Task Explicit_search_starts_the_real_browser_exactly_once_and_the_host_stops_it_on_exit()
    {
        using var composition = new IsolatedAppComposition();
        var browser = new FakeMdnsServiceBrowser();
        composition.Services.AddSingleton<IMdnsServiceBrowser>(browser);
        await using var provider = composition.BuildProvider();
        var hosted = Assert.Single(DiscoveryHostedServices(composition.Services, provider));
        await hosted.StartAsync(CancellationToken.None);

        var activity = Assert.IsAssignableFrom<IServerDiscoveryActivity>(provider.GetRequiredService<IServerDiscoveryService>());
        await Task.WhenAll(activity.StartSearchAsync(), activity.StartSearchAsync());
        await activity.StartSearchAsync();

        Assert.Equal(1, browser.StartCount);
        Assert.Equal(1, browser.FoundSubscriberCount);
        Assert.True(activity.IsSearching);

        await hosted.StopAsync(CancellationToken.None);
        Assert.Equal(1, browser.StopCount);
        Assert.False(activity.IsSearching);
    }

    /// <summary>Runs every hosted-service factory against a recording provider first (constructs nothing), then
    /// resolves ONLY the participants that resolve the discovery service - never tray, notifications or the engine.</summary>
    private static IHostedService[] DiscoveryHostedServices(IServiceCollection services, IServiceProvider provider) =>
    [
        .. services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Where(descriptor => FirstRequest(descriptor) == typeof(ServerDiscoveryService))
            .Select(descriptor => (IHostedService)descriptor.ImplementationFactory!(provider))
    ];

    private static Type? FirstRequest(ServiceDescriptor descriptor)
    {
        Assert.NotNull(descriptor.ImplementationFactory);
        var recorder = new RequestRecordingProvider();
        try
        {
            descriptor.ImplementationFactory!(recorder);
        }
        catch (RequestRecordingProvider.Stop)
        {
        }

        return recorder.Requested.FirstOrDefault();
    }

    // ------------------------------------------------------------------ the only caller is the user action

    [Fact]
    public void Only_the_dashboard_action_and_the_hide_path_drive_the_search_lifecycle_in_product_code()
    {
        var root = RepositoryRoot();
        var source = Path.Combine(root, "src");
        var qa = Path.Combine(source, "ServerMonitor.App", "Qa");

        var startCallers = CallersOf(source, qa, @"\.StartSearchAsync\s*\(");
        var stopCallers = CallersOf(source, qa, @"\.StopSearchAsync\s*\(");

        Assert.Equal([Path.Combine("src", "ServerMonitor.App", "ViewModels", "DashboardViewModel.cs")], startCallers);
        Assert.Equal([Path.Combine("src", "ServerMonitor.App", "Services", "LocalNetworkSearchLifetime.cs")], stopCallers);
    }

    [Fact]
    public void Both_hide_paths_of_the_window_controller_stop_the_search()
    {
        var text = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "ServerMonitor.App", "Services", "ApplicationWindowController.cs"));

        foreach (var method in new[] { "HideToBackgroundCore", "HideForMinimizeCore" })
        {
            var start = text.IndexOf("private void " + method + "()", StringComparison.Ordinal);
            Assert.True(start >= 0, method + " not found");
            var end = text.IndexOf("});", start, StringComparison.Ordinal);
            Assert.Contains(
                "LocalNetworkSearchLifetime.StopForBackground(discoveryService, logger);",
                text[start..end],
                StringComparison.Ordinal);
        }
    }

    private static string[] CallersOf(string source, string excluded, string pattern) =>
    [
        .. Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(excluded + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => Regex.IsMatch(File.ReadAllText(path), pattern))
            .Select(path => Path.GetRelativePath(RepositoryRoot(), path))
            .Order(StringComparer.Ordinal)
    ];

    // ------------------------------------------------------------------ background stop

    [Fact]
    public async Task Hiding_stops_a_running_search_and_a_later_explicit_search_restarts_it()
    {
        var browser = new FakeMdnsServiceBrowser();
        await using var service = Service(browser);
        await service.StartSearchAsync();

        await LocalNetworkSearchLifetime.StopForBackground(service, NullLogger.Instance);

        Assert.False(service.IsSearching);
        Assert.Equal(1, browser.StopCount);
        Assert.Equal(0, browser.FoundSubscriberCount);

        await service.StartSearchAsync();
        Assert.Equal(2, browser.StartCount);
    }

    [Fact]
    public async Task Hiding_without_live_discovery_or_with_a_failing_stop_is_a_quiet_no_op()
    {
        await LocalNetworkSearchLifetime.StopForBackground(new NullServerDiscoveryService(), NullLogger.Instance);
        await LocalNetworkSearchLifetime.StopForBackground(new ThrowingActivity(), NullLogger.Instance);
    }

    [Fact]
    public async Task Hiding_while_a_start_is_in_flight_still_ends_with_no_listener()
    {
        var store = new GatedIgnoredDeviceStore(open: false);
        var browser = new FakeMdnsServiceBrowser();
        await using var service = Service(browser, store);

        var start = service.StartSearchAsync();
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var stop = LocalNetworkSearchLifetime.StopForBackground(service, NullLogger.Instance);
        store.Release();
        await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(service.IsSearching);
        Assert.Equal(browser.StartCount, browser.StopCount);
    }

    // ------------------------------------------------------------------ view wiring + copy

    [Fact]
    public void Empty_state_view_binds_the_action_to_the_command_and_its_visibility_to_the_state()
    {
        var xaml = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "ServerMonitor.App", "Views", "DashboardPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var button = Assert.Single(xaml.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "EmptyDiscoveryStartButton"));
        Assert.Equal("HyperlinkButton", button.Name.LocalName);
        Assert.Equal("{x:Bind ViewModel.StartLocalNetworkSearchCommand}", (string?)button.Attribute("Command"));
        Assert.Equal("DashboardEmptyDiscoveryStartButton", (string?)button.Attribute(x + "Uid"));

        var block = Assert.Single(xaml.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "EmptyDiscoveryStartBlock"));
        Assert.Equal("{x:Bind ViewModel.CanStartLocalNetworkSearch, Mode=OneWay}", (string?)block.Attribute("Visibility"));
        Assert.Contains(block.Descendants(), e => (string?)e.Attribute(x + "Uid") == "DashboardEmptyDiscoveryPermissionNote");

        // Inside the first-server empty state, i.e. only reachable from a visible window.
        Assert.Contains(block.Ancestors(), e => (string?)e.Attribute(x + "Name") == "FirstServerStateBlock");
    }

    [Theory]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public void Every_language_carries_the_action_and_the_permission_note(string language)
    {
        var resw = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "ServerMonitor.App", "Resources", language, "Resources.resw"));
        var values = resw.Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);

        Assert.False(string.IsNullOrWhiteSpace(values["DashboardEmptyDiscoveryStartButton.Content"]));
        var note = values["DashboardEmptyDiscoveryPermissionNote.Text"];
        Assert.Contains("Windows", note, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static ServerDiscoveryService Service(FakeMdnsServiceBrowser browser, IIgnoredDeviceStore? store = null) =>
        new(browser, store ?? new GatedIgnoredDeviceStore(), NullLogger<ServerDiscoveryService>.Instance);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class ThrowingActivity : IServerDiscoveryService, IServerDiscoveryActivity
    {
        public event EventHandler? DiscoveredChanged { add { } remove { } }

        public event EventHandler? IsSearchingChanged { add { } remove { } }

        public bool IsSearching => true;

        public IReadOnlyList<DiscoveredService> GetDiscovered() => [];

        public Task IgnoreAsync(ServiceInstanceIdentity identity, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResetIgnoredAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StartSearchAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic");

        public Task StopSearchAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic");
    }
}
