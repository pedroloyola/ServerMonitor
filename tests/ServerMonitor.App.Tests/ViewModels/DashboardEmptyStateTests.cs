using Microsoft.Extensions.Logging.Abstractions;
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

    private sealed class RecordingDialogService : IServerDialogService
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
