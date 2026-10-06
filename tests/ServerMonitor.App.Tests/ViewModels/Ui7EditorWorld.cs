using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Core.SshConfig;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.7A test world. Everything that carries a UI.7 guarantee is the PRODUCTION component: the navigation service (UI.4
/// seam over a recorded host), the editor session, the page controller, the editor view model, the dashboard's persist
/// path, <see cref="ServerProfileService"/>, <see cref="ServerService"/>, the JSON server repository and both JSON trust
/// stores - all over a temp directory (never a real user path). Doubles: the SSH service (scripted, with a barrier), the
/// credential store (in-memory, RECORDING every write/delete), the discard prompt (scripted answer), and the XAML page
/// itself (<see cref="EditorPageDouble"/>, which mirrors ServerEditorPage's code-behind over the real controller).
/// </summary>
internal sealed class Ui7EditorWorld : IDisposable
{
    private readonly Dictionary<Type, object> _singletonPages = [];

    public Ui7EditorWorld(ILocalSshKeyDiscovery? keyDiscovery = null)
    {
        Directory = Path.Combine(Path.GetTempPath(), "servermonitor-ui7-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        var gate = Gate;
        StorageOptions = new ServerStorageOptions { FilePath = Path.Combine(Directory, "servers.json") };
        Repository = new JsonServerRepository(StorageOptions, NullLogger<JsonServerRepository>.Instance, gate);
        ServerService = new ServerService(Repository, new ServerValidator(), gate);
        Credentials = new RecordingCredentialStore();
        Profiles = new ServerProfileService(ServerService, Credentials, gate);
        var trustOptions = new HostKeyTrustStorageOptions { FilePath = Path.Combine(Directory, "known-hosts.json") };
        DirectTrustFile = trustOptions.FilePath;
        RoutedTrustFile = RoutedHostKeyTrustStorageOptions.From(trustOptions).FilePath;
        DirectTrust = new RecordingDirectTrust(new JsonHostKeyTrustStore(trustOptions, NullLogger<JsonHostKeyTrustStore>.Instance, gate));
        RoutedTrust = new RecordingRoutedTrust(new JsonRoutedHostKeyTrustStore(
            RoutedHostKeyTrustStorageOptions.From(trustOptions), NullLogger<JsonRoutedHostKeyTrustStore>.Instance, gate));
        Ssh = new ScriptedSsh();
        Navigation = new NavigationService(new DashboardProvider(this), NullLogger<NavigationService>.Instance, CreatePage);
        Navigation.Initialize(Host);
        Session = new ServerEditorSession(
            new ServerValidator(),
            Ssh,
            DirectTrust,
            RoutedTrust,
            ConnectionStates,
            new NullPicker(),
            new FakeLocalizationService(),
            new EmptyImportSource(),
            keyDiscovery ?? new NoKeys(),
            Navigation,
            ReturnFocus,
            NullLogger<ServerEditorSession>.Instance,
            () => OpenerName);
        Dashboard = new DashboardViewModel(
            ServerService,
            Profiles,
            new NoRemoveDialogs(),
            Session,
            ConnectionStates,
            new FakeServerMetricsStore(),
            new ServerMonitoringStateStore(),
            new FakeMonitoringEngine(),
            new NullServerDiscoveryService(),
            Navigation,
            new FakeLocalizationService(),
            NullLogger<DashboardViewModel>.Instance,
            clock: new PresentationClock(new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero))));
    }

    public string Directory { get; }

    public ConfigurationWriteGate Gate { get; } = new();

    public ServerStorageOptions StorageOptions { get; }

    public JsonServerRepository Repository { get; }

    public ServerService ServerService { get; }

    public RecordingCredentialStore Credentials { get; }

    public ServerProfileService Profiles { get; }

    public string DirectTrustFile { get; }

    public string RoutedTrustFile { get; }

    public RecordingDirectTrust DirectTrust { get; }

    public RecordingRoutedTrust RoutedTrust { get; }

    public ScriptedSsh Ssh { get; }

    public FakeConnectionStateStore ConnectionStates { get; } = new();

    public RecordingHost Host { get; } = new();

    public NavigationService Navigation { get; }

    public ServerEditorReturnFocus ReturnFocus { get; } = new();

    public ServerEditorSession Session { get; }

    public DashboardViewModel Dashboard { get; }

    public ScriptedDiscardPrompt Prompt { get; } = new();

    /// <summary>The x:Name the "focused opener" capture reports when an editor opens.</summary>
    public string? OpenerName { get; set; } = "OverviewAddButton";

    public List<EditorPageDouble> EditorPages { get; } = [];

    /// <summary>Per editor page load: were all earlier editor pages (and so their view models) already disposed?</summary>
    public List<bool> PreviousEditorsDisposedAtLoad { get; } = [];

    public EditorPageDouble Page => Assert.IsType<EditorPageDouble>(Host.Content);

    public ServerEditorViewModel ViewModel => Page.Controller.ViewModel!;

    /// <summary>Starts at the Visão geral, like the shell.</summary>
    public async Task StartAsync()
    {
        await Dashboard.LoadAsync();
        Navigation.GoToDashboard();
    }

    /// <summary>A saved server through the production write path (the seed for Edit).</summary>
    public async Task<Server> SeedAsync(
        string name = "web-01",
        AuthenticationMethod authentication = AuthenticationMethod.SshKey,
        string? password = null,
        ServerRoute? route = null,
        string? jumpPassword = null)
    {
        using var secret = password is null ? null : new SecretValue(password);
        using var jumpSecret = jumpPassword is null ? null : new SecretValue(jumpPassword);
        var result = await Profiles.AddAsync(new ServerProfileInput
        {
            Configuration = new ServerInput
            {
                Name = name,
                Host = "10.0.0.5",
                Port = 22,
                Username = "deploy",
                OperatingSystem = ServerOperatingSystem.Linux,
                AuthenticationMethod = authentication,
                PrivateKeyPath = authentication == AuthenticationMethod.SshKey ? Path.Combine(Directory, "id_ed25519") : null,
                Route = route
            },
            CredentialChange = secret is null ? CredentialChange.Clear : CredentialChange.Replace(secret),
            JumpCredentialChange = jumpSecret is null ? null : CredentialChange.Replace(jumpSecret)
        });
        Assert.True(result.Succeeded);
        await Dashboard.LoadAsync();
        Credentials.ResetCounts();
        DirectTrust.ResetCounts();
        RoutedTrust.ResetCounts();
        return result.Server!;
    }

    /// <summary>Opens the editor for that saved server from its Detail page (the only Edit surface).</summary>
    public Task OpenEditAsync(Guid serverId)
    {
        Navigation.GoToServerDetail(serverId, ServerDetailOrigin.Servers);
        var card = Dashboard.VisibleServers.Single(candidate => candidate.Server.Id == serverId);
        return ((AsyncRelayCommand)card.EditCommand).ExecuteAsync();
    }

    /// <summary>The bytes of every persisted store the editor could touch (servers, routed servers, both trust files).</summary>
    public IReadOnlyDictionary<string, string?> PersistedSnapshot() => new Dictionary<string, string?>
    {
        ["servers"] = Read(StorageOptions.FilePath),
        ["routed-servers"] = Read(StorageOptions.RoutedFilePath),
        ["known-hosts"] = Read(DirectTrustFile),
        ["known-hosts.routes"] = Read(RoutedTrustFile)
    };

    private static string? Read(string path) => File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null;

    public void Dispose()
    {
        Repository.Dispose();
        ServerService.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private object CreatePage(Type type)
    {
        if (type == typeof(ServerEditorPage))
        {
            var page = new EditorPageDouble(Session, Prompt);
            var earlier = EditorPages.ToList();
            page.BeforeLoad = () => PreviousEditorsDisposedAtLoad.Add(earlier.All(previous => previous.Disposed));
            EditorPages.Add(page);
            return page;
        }

        if (type == typeof(ServerDetailPage))
        {
            return new DetailPageDouble();
        }

        if (type == typeof(HistoryPage))
        {
            return new HistoryPageDouble();
        }

        if (type == typeof(ServersPage) || type == typeof(WorkloadsPage))
        {
            return new PerVisitPageDouble(type);
        }

        // Singletons (Visão geral, Definições): the same instance for every request, like the container.
        if (!_singletonPages.TryGetValue(type, out var singleton))
        {
            singleton = new SingletonPageDouble(type);
            _singletonPages[type] = singleton;
        }

        return singleton;
    }

    private sealed class DashboardProvider(Ui7EditorWorld world) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(DashboardViewModel) ? world.Dashboard : null;
    }

    internal sealed class RecordingHost : INavigationHost
    {
        public object? Content { get; set; }
    }

    internal sealed class SingletonPageDouble(Type type) : ISettingsNavigationTarget
    {
        public Type PageType { get; } = type;

        public bool IsReadyForSectionRequest => false;

        public void OnNavigatedToAgain()
        {
        }
    }

    internal sealed class PerVisitPageDouble(Type type) : IDisposable
    {
        public Type PageType { get; } = type;

        public void Dispose()
        {
        }
    }

    internal sealed class DetailPageDouble : IServerDetailView, IDisposable
    {
        public Guid ServerId { get; private set; }

        public void Load(Guid serverId, ServerDetailOrigin origin) => ServerId = serverId;

        public void Dispose()
        {
        }
    }

    internal sealed class HistoryPageDouble : IHistoryView, IDisposable
    {
        public void Load(Guid? serverId, string serverName, bool fromDetail)
        {
        }

        public void LoadSidebar(Guid? lastDetailServer)
        {
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// The XAML-free double of ServerEditorPage: the SAME controller calls the real page makes (Load, exit guard, Cancel, Esc,
/// the single submit path, Dispose), with the password boxes reduced to strings that are read, staged and cleared the
/// way the page's ServerFormControl does.
/// </summary>
internal sealed class EditorPageDouble : IServerEditorView, INavigationExitGuard, IDisposable
{
    public EditorPageDouble(IServerEditorSession session, IServerEditorDiscardPrompt prompt)
    {
        Controller = new ServerEditorPageController(session, prompt) { HasTypedSecret = () => TypedPassword.Length > 0 };
    }

    public ServerEditorPageController Controller { get; }

    /// <summary>What the user typed into the active password box (not yet seen by the view model).</summary>
    public string TypedPassword { get; set; } = string.Empty;

    public bool Disposed { get; private set; }

    /// <summary>Runs as navigation loads the page (after it replaced the previous one, before the view model exists).</summary>
    public Action? BeforeLoad { get; set; }

    public void Load(ServerEditorRequest request)
    {
        BeforeLoad?.Invoke();
        Controller.Load(request);
    }

    public Task<bool> ConfirmLeaveAsync() => Controller.ConfirmLeaveAsync();

    public Task<ServerEditorSubmitOutcome?> SubmitAsync() => Controller.SubmitAsync(CaptureSecrets);

    public async Task TestAsync()
    {
        CaptureSecrets();
        await Controller.ViewModel!.TestConnectionAsync();
    }

    public void Dispose()
    {
        Disposed = true;
        TypedPassword = string.Empty;
        Controller.Dispose();
    }

    private void CaptureSecrets()
    {
        Controller.ViewModel!.CaptureSecret(TypedPassword);
        TypedPassword = string.Empty;
    }
}

internal sealed class ScriptedDiscardPrompt : IServerEditorDiscardPrompt
{
    private TaskCompletionSource<bool>? _pending;

    public List<ServerEditorDiscardContext> Asked { get; } = [];

    /// <summary>The answer given at once; null = held until <see cref="Answer"/> (synchronous continuations).</summary>
    public bool? AutoAnswer { get; set; } = true;

    public Task<bool> ConfirmDiscardAsync(ServerEditorDiscardContext context)
    {
        Asked.Add(context);
        if (AutoAnswer is { } answer)
        {
            return Task.FromResult(answer);
        }

        _pending = new TaskCompletionSource<bool>();
        return _pending.Task;
    }

    public void Answer(bool discard) => (_pending ?? throw new InvalidOperationException("No question is pending.")).SetResult(discard);
}

/// <summary>Scripted SSH: a fixed result, optionally held by a barrier that only the test (or cancellation) releases.</summary>
internal sealed class ScriptedSsh : ISshConnectionService
{
    private TaskCompletionSource<SshConnectionResult>? _barrier;

    public SshConnectionResult Result { get; set; } = TestData.Connected();

    public bool Hold { get; set; }

    public int TestConnectionCount { get; private set; }

    public List<SshConnectionRequest> Requests { get; } = [];

    /// <summary>Signalled when a held test has arrived at the barrier.</summary>
    public TaskCompletionSource Arrived { get; private set; } = new();

    public void Release(SshConnectionResult? result = null) =>
        (_barrier ?? throw new InvalidOperationException("No test is held.")).SetResult(result ?? Result);

    public Task<SshConnectionResult> ConnectAsync(SshConnectionRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<SshConnectionResult> DetectOperatingSystemAsync(SshConnectionRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<SshConnectionResult> TestConnectionAsync(SshConnectionRequest request, CancellationToken cancellationToken = default)
    {
        TestConnectionCount++;
        Requests.Add(request);
        if (!Hold)
        {
            return Task.FromResult(Result);
        }

        _barrier = new TaskCompletionSource<SshConnectionResult>();
        var arrived = Arrived;
        Arrived = new TaskCompletionSource();
        arrived.SetResult();
        return _barrier.Task.WaitAsync(cancellationToken);
    }
}

internal sealed class RecordingCredentialStore : IServerCredentialStore
{
    private readonly Dictionary<CredentialReference, string> _secrets = [];

    public int Writes { get; private set; }

    public int Deletes { get; private set; }

    public int Reads { get; private set; }

    public int Count => _secrets.Count;

    public void ResetCounts() => Writes = Deletes = Reads = 0;

    public Task WriteAsync(CredentialReference reference, SecretValue secret, CancellationToken cancellationToken = default)
    {
        Writes++;
        _secrets[reference] = new string(secret.Reveal());
        return Task.CompletedTask;
    }

    public Task<SecretValue?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default)
    {
        Reads++;
        return Task.FromResult(_secrets.TryGetValue(reference, out var value) ? new SecretValue(value) : null);
    }

    public Task<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default)
    {
        Deletes++;
        return Task.FromResult(_secrets.Remove(reference));
    }
}

/// <summary>The real direct trust store, with every write counted and (optionally) held by a barrier.</summary>
internal sealed class RecordingDirectTrust(IHostKeyTrustStore inner) : IHostKeyTrustStore
{
    public int Trusts { get; private set; }

    public TaskCompletionSource? Barrier { get; set; }

    public void ResetCounts() => Trusts = 0;

    public Task<TrustedHostKey?> GetAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) =>
        inner.GetAsync(endpoint, cancellationToken);

    public async Task TrustAsync(SshEndpoint endpoint, HostKeyIdentity identity, CancellationToken cancellationToken = default)
    {
        Trusts++;
        if (Barrier is { } barrier)
        {
            await barrier.Task;
        }

        await inner.TrustAsync(endpoint, identity, cancellationToken);
    }

    public Task<bool> RemoveAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) =>
        inner.RemoveAsync(endpoint, cancellationToken);
}

internal sealed class RecordingRoutedTrust(IRoutedHostKeyTrustStore inner) : IRoutedHostKeyTrustStore
{
    public int Trusts { get; private set; }

    public void ResetCounts() => Trusts = 0;

    public Task<TrustedRoutedHostKey?> GetAsync(SshRoute route, CancellationToken cancellationToken = default) =>
        inner.GetAsync(route, cancellationToken);

    public Task TrustAsync(SshRoute route, HostKeyIdentity identity, CancellationToken cancellationToken = default)
    {
        Trusts++;
        return inner.TrustAsync(route, identity, cancellationToken);
    }

    public Task<bool> RemoveAsync(SshRoute route, CancellationToken cancellationToken = default) =>
        inner.RemoveAsync(route, cancellationToken);
}

internal sealed class NullPicker : IPrivateKeyFilePicker
{
    public Task<string?> PickAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}

internal sealed class EmptyImportSource : ISshConfigImportSource
{
    public Task<SshConfigImportResult> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(SshConfigImportResult.NotFound);
}

internal sealed class NoKeys : ILocalSshKeyDiscovery
{
    public Task<IReadOnlyList<LocalSshKey>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocalSshKey>>([]);
}

internal sealed class OneRecommendedKey(string path) : ILocalSshKeyDiscovery
{
    public Task<IReadOnlyList<LocalSshKey>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocalSshKey>>([new LocalSshKey(path, Path.GetFileName(path), LocalSshKeyKind.Ed25519, IsRecommended: true)]);
}

internal sealed class NoRemoveDialogs : IServerDialogService
{
    public Task<bool> ConfirmRemoveAsync(Server server) => Task.FromResult(false);
}
