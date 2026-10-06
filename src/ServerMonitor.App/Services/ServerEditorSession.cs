using Microsoft.Extensions.Logging;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Services;

/// <summary>
/// UI.7 B-3: the single lifetime owner of the server editor (process singleton). It creates the ONE form view model of a
/// visit (the same factory the modal used), hands it to that visit's page, waits for the opener's persist result before
/// it leaves (B-4), and disposes the view model when the page goes - whatever took it away (Save, Cancel, Esc, Back,
/// sidebar, breadcrumb, activation, a second editor) and also when opening fails. It never persists anything itself.
/// Logs carry only the mode and exception types (B-21).
/// </summary>
public sealed class ServerEditorSession : IServerEditorSession
{
    private readonly IServerValidator _validator;
    private readonly ISshConnectionService _sshConnectionService;
    private readonly IHostKeyTrustStore _hostKeyTrustStore;
    private readonly IRoutedHostKeyTrustStore _routedHostKeyTrustStore;
    private readonly IServerConnectionStateStore _connectionStateStore;
    private readonly IPrivateKeyFilePicker _privateKeyFilePicker;
    private readonly ILocalizationService _localizationService;
    private readonly ISshConfigImportSource _sshConfigImportSource;
    private readonly ILocalSshKeyDiscovery _localSshKeyDiscovery;
    private readonly INavigationService _navigation;
    private readonly ServerEditorReturnFocus _returnFocus;
    private readonly ILogger<ServerEditorSession> _logger;
    private readonly Func<string?> _captureFocusedElementName;
    private readonly Dictionary<ServerEditorRequest, Visit> _visits = new(ReferenceEqualityComparer.Instance);
    private Visit? _live;

    public ServerEditorSession(
        IServerValidator validator,
        ISshConnectionService sshConnectionService,
        IHostKeyTrustStore hostKeyTrustStore,
        IRoutedHostKeyTrustStore routedHostKeyTrustStore,
        IServerConnectionStateStore connectionStateStore,
        IPrivateKeyFilePicker privateKeyFilePicker,
        ILocalizationService localizationService,
        ISshConfigImportSource sshConfigImportSource,
        ILocalSshKeyDiscovery localSshKeyDiscovery,
        INavigationService navigation,
        ServerEditorReturnFocus returnFocus,
        ILogger<ServerEditorSession> logger,
        Func<string?>? captureFocusedElementName = null)
    {
        _validator = validator;
        _sshConnectionService = sshConnectionService;
        _hostKeyTrustStore = hostKeyTrustStore;
        _routedHostKeyTrustStore = routedHostKeyTrustStore;
        _connectionStateStore = connectionStateStore;
        _privateKeyFilePicker = privateKeyFilePicker;
        _localizationService = localizationService;
        _sshConfigImportSource = sshConfigImportSource;
        _localSshKeyDiscovery = localSshKeyDiscovery;
        _navigation = navigation;
        _returnFocus = returnFocus;
        _logger = logger;
        _captureFocusedElementName = captureFocusedElementName ?? (() => null);
    }

    /// <summary>Test probe: the number of view models this session created (never two alive, CP-13 / R-13).</summary>
    internal int CreatedViewModels { get; private set; }

    /// <summary>Visits not ended yet (tests: a visit never hangs after a failed navigation).</summary>
    internal int OpenVisits => _visits.Count;

    /// <summary>Test probe: the view model of the live visit, if any.</summary>
    internal ServerEditorViewModel? LiveViewModel => _live?.ViewModel;

    public Task OpenAddAsync(ServerEditorPersist persist) =>
        OpenAsync(ServerEditorMode.Add, existing: null, prefill: null, openSshImport: false, persist);

    public Task OpenEditAsync(Server server, ServerEditorPersist persist)
    {
        ArgumentNullException.ThrowIfNull(server);
        return OpenAsync(ServerEditorMode.Edit, server, prefill: null, openSshImport: false, persist);
    }

    public Task OpenDiscoveryAsync(ServerDiscoveryPrefill prefill, ServerEditorPersist persist)
    {
        ArgumentNullException.ThrowIfNull(prefill);
        return OpenAsync(ServerEditorMode.Add, existing: null, prefill, openSshImport: false, persist);
    }

    public Task OpenSshImportAsync(ServerEditorPersist persist) =>
        OpenAsync(ServerEditorMode.Add, existing: null, prefill: null, openSshImport: true, persist);

    private Task OpenAsync(
        ServerEditorMode mode,
        Server? existing,
        ServerDiscoveryPrefill? prefill,
        bool openSshImport,
        ServerEditorPersist persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        var request = new ServerEditorRequest(mode, existing, prefill, openSshImport, OriginFor(existing));
        var visit = new Visit(request, persist, CaptureTrigger());
        _visits[request] = visit;
        try
        {
            // Through the exit guard of whatever page is shown (another editor included). Refused or still deciding when
            // another navigation arrives: the visit ends here and no view model is ever created for it.
            _navigation.GoToServerEditor(request, refused: () => End(visit));
        }
        catch
        {
            End(visit);
            throw;
        }

        return visit.Completion.Task;
    }

    public ServerEditorViewModel? Attach(ServerEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_visits.TryGetValue(request, out var visit) || visit.Ended)
        {
            return null;
        }

        if (visit.ViewModel is { } existing)
        {
            return existing;
        }

        // One live editor: navigation has already replaced (and so detached) the previous page; this is the backstop.
        if (_live is { } previous && !ReferenceEquals(previous, visit))
        {
            End(previous);
        }

        var viewModel = new ServerEditorViewModel(
            _validator,
            _sshConnectionService,
            _hostKeyTrustStore,
            _connectionStateStore,
            _privateKeyFilePicker,
            _localizationService,
            request.Existing,
            request.Prefill,
            _sshConfigImportSource,
            _routedHostKeyTrustStore,
            _localSshKeyDiscovery);
        CreatedViewModels++;
        visit.ViewModel = viewModel;
        _live = visit;
        if (request.OpenSshImport)
        {
            // Same read-only load as the page's import button; it reports its own failures in the form.
            _ = viewModel.LoadSshConfigHostsAsync();
        }

        return viewModel;
    }

    public async Task<ServerEditorSaveOutcome> SaveAsync(ServerEditorRequest request, ServerEditorResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        using (result)
        {
            if (!_visits.TryGetValue(request, out var visit) || visit.Ended || visit.Saved)
            {
                return new ServerEditorSaveOutcome(ServerEditorSaveStatus.NotCurrent);
            }

            var secretsConsumed = result.Profile.CredentialChange.Secret is not null
                || result.Profile.JumpCredentialChange?.Secret is not null;
            ServerOperationResult persisted;
            try
            {
                persisted = await visit.Persist(result);
            }
            catch (ConfigurationLockedException)
            {
                return new ServerEditorSaveOutcome(ServerEditorSaveStatus.ConfigurationLocked, secretsConsumed);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Could not save the server editor ({Mode}). Exception type: {ExceptionType}.",
                    request.Mode,
                    exception.GetType().Name);
                return new ServerEditorSaveOutcome(ServerEditorSaveStatus.Failed, secretsConsumed);
            }

            if (!persisted.Succeeded)
            {
                return new ServerEditorSaveOutcome(ServerEditorSaveStatus.Failed, secretsConsumed);
            }

            visit.Saved = true;
            visit.SavedServerId = persisted.Server!.Id;
            GoToSavedDestination(visit);
            return new ServerEditorSaveOutcome(ServerEditorSaveStatus.Saved, secretsConsumed);
        }
    }

    public void ResumeSavedDestination(ServerEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_visits.TryGetValue(request, out var visit))
        {
            GoToSavedDestination(visit);
        }
    }

    // B-5: the saved server's Detail page (its page lets go without asking: the visit saved).
    private void GoToSavedDestination(Visit visit)
    {
        if (visit.Ended || !visit.Saved || visit.SavedServerId is not { } serverId)
        {
            return;
        }

        var request = visit.Request;
        if (request.Mode == ServerEditorMode.Edit)
        {
            _navigation.ReturnToServerDetail(serverId);
        }
        else
        {
            _navigation.GoToServerDetail(
                serverId,
                request.Origin.Destination == NavigationDestination.Servers ? ServerDetailOrigin.Servers : ServerDetailOrigin.Overview);
        }
    }

    public bool IsSaved(ServerEditorRequest request) =>
        !_visits.TryGetValue(request, out var visit) || visit.Saved || visit.Ended;

    public void Leave(ServerEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_visits.TryGetValue(request, out var visit) || visit.Ended)
        {
            return;
        }

        // The guard asks first (dirty → "Descartar alterações?"); only an accepted exit remembers the return focus.
        _navigation.LeaveCurrentPageThen(() =>
        {
            var origin = request.Origin;
            _returnFocus.Remember(origin.Destination, visit.TriggerName);
            switch (origin.Destination)
            {
                case NavigationDestination.Detail when origin.ServerId != Guid.Empty:
                    _navigation.ReturnToServerDetail(origin.ServerId);
                    break;
                case NavigationDestination.Servers:
                    _navigation.GoToServers();
                    break;
                default:
                    _navigation.GoToDashboard();
                    break;
            }
        });
    }

    public void Detach(ServerEditorRequest request)
    {
        if (request is not null && _visits.TryGetValue(request, out var visit))
        {
            End(visit);
        }
    }

    private void End(Visit visit)
    {
        if (visit.Ended)
        {
            return;
        }

        visit.Ended = true;
        _visits.Remove(visit.Request);
        if (ReferenceEquals(_live, visit))
        {
            _live = null;
        }

        try
        {
            visit.ViewModel?.Dispose();
        }
        finally
        {
            visit.Completion.TrySetResult();
        }
    }

    private ServerEditorOrigin OriginFor(Server? existing)
    {
        if (existing is not null)
        {
            // An edit only comes from its Server Detail page (Cortex §1.1): Back returns there.
            return new ServerEditorOrigin(NavigationDestination.Detail, existing.Id);
        }

        return _navigation.CurrentDestination switch
        {
            NavigationDestination.Servers => new ServerEditorOrigin(NavigationDestination.Servers),
            NavigationDestination.ServerEditor when _live is { } live => live.Request.Origin with { },
            _ => ServerEditorOrigin.Overview
        };
    }

    private string? CaptureTrigger()
    {
        try
        {
            return _captureFocusedElementName();
        }
        catch (Exception exception)
        {
            // No window yet (headless) or no focus: the origin page focuses its heading instead.
            _logger.LogDebug("No focus origin for the server editor. Exception type: {ExceptionType}.", exception.GetType().Name);
            return null;
        }
    }

    private sealed class Visit(ServerEditorRequest request, ServerEditorPersist persist, string? triggerName)
    {
        public ServerEditorRequest Request { get; } = request;

        public ServerEditorPersist Persist { get; } = persist;

        public string? TriggerName { get; } = triggerName;

        // Asynchronous continuations: the opener resumes after the navigation that ended the visit has finished.
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ServerEditorViewModel? ViewModel { get; set; }

        public bool Saved { get; set; }

        public Guid? SavedServerId { get; set; }

        public bool Ended { get; set; }
    }
}
