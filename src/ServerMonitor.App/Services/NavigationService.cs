using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;

namespace ServerMonitor.App.Services;

public sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NavigationService> _logger;
    private readonly Func<Type, object> _pageFactory;
    private INavigationHost? _host;
    private readonly Dictionary<Guid, ServerDetailOrigin> _detailOrigins = [];
    private Guid? _lastDetailServer;
    private bool _historyFromSidebar;
    // UI.7 H-UI7-3: true while a navigation the exit guard accepted runs (its own nested navigations are not re-asked).
    private bool _leaving;
    // UI.7: an exit decision (the discard dialog) is open; any other navigation meanwhile is refused, never queued.
    private bool _exitDecisionPending;
    // m-2: the latest external activation that arrived while the discard question was open (latest-wins).
    private Action? _pendingActivation;
    public NavigationDestination? CurrentDestination { get; private set; }
    public event EventHandler? Navigated;

    public void EnsureInitialNavigation()
    {
        if (Host.Content is null) GoToDashboard();
    }

    public NavigationService(IServiceProvider serviceProvider, ILogger<NavigationService> logger)
        : this(serviceProvider, logger, pageFactory: null)
    {
    }

    /// <summary>
    /// TEST SEAM (UI.4 Cortex r1 MUST-1): the REAL navigation logic over a recorded host and page factory, so the order
    /// "content, then Load" and the server-exists check are provable without a XAML runtime. Production passes null and
    /// resolves pages from the container.
    /// </summary>
    internal NavigationService(IServiceProvider serviceProvider, ILogger<NavigationService> logger, Func<Type, object>? pageFactory)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _pageFactory = pageFactory ?? serviceProvider.GetRequiredService;
    }

    /// <inheritdoc />
    public event EventHandler? NavigatedAwayFromOverview;

    public void Initialize(Frame frame) => _host = new FrameHost(frame);

    internal void Initialize(INavigationHost host) => _host = host;

    public void NavigateTo<TPage>() where TPage : Page
    {
        if (Host.Content is TPage || CurrentDestination == DestinationFor(typeof(TPage)))
        {
            return;
        }

        if (DeferToExitGuard(NavigateTo<TPage>))
        {
            return;
        }

        Show(_pageFactory(typeof(TPage)), DestinationFor(typeof(TPage)));
        _logger.LogInformation("Navigated to {Page}.", typeof(TPage).Name);
    }

    public void GoToDashboard() => NavigateTo<DashboardPage>();

    public void GoToSettings() => GoToSettings(SettingsSection.General);

    public void GoToSettings(SettingsSection section)
    {
        _ = Host;
        if (section == SettingsSection.About)
        {
            Interlocked.Exchange(ref _aboutSettingsFocusRequested, 1);
        }

        var pageType = section == SettingsSection.General ? typeof(SettingsPage) : typeof(SettingsDataPage);

        // Both sub-pages are singletons: the factory hands back the very instance already shown, if it is.
        var page = _pageFactory(pageType);
        if (!ReferenceEquals(Host.Content, page) && DeferToExitGuard(() => GoToSettings(section)))
        {
            return;
        }

        if (ReferenceEquals(Host.Content, page))
        {
            // Cortex #6: no content swap means no Loaded — notify the page so a pending section request is honoured now.
            if (page is ISettingsNavigationTarget { IsReadyForSectionRequest: true } shown)
            {
                shown.OnNavigatedToAgain();
            }
            return;
        }

        Show(page, DestinationFor(pageType));
        _logger.LogInformation("Navigated to Settings ({Section}).", section);
    }

    private int _backgroundSettingsFocusRequested;
    private int _aboutSettingsFocusRequested;

    public void RequestBackgroundSettingsFocus()
    {
        Interlocked.Exchange(ref _backgroundSettingsFocusRequested, 1);

        // Cortex #6: the General sub-page may already be the content (ApplicationWindowController navigates first, then
        // requests). It then gets no Loaded, so it is told now; otherwise its next Loaded consumes the request. (The Data
        // sub-page only consumes the About request, so this one stays pending for General.)
        // Cortex B1 M-1: never a page whose Loaded has not run yet (cold path) - its Loaded consumes the request instead.
        if (_host?.Content is ISettingsNavigationTarget { IsReadyForSectionRequest: true } target)
        {
            target.OnNavigatedToAgain();
        }
    }

    public bool ConsumeBackgroundSettingsFocus() =>
        Interlocked.Exchange(ref _backgroundSettingsFocusRequested, 0) == 1;

    public bool ConsumeAboutSettingsFocus() =>
        Interlocked.Exchange(ref _aboutSettingsFocusRequested, 0) == 1;

    public void GoToHistory(Guid serverId, string serverName)
    {
        _ = Host;
        if (DeferToExitGuard(() => GoToHistory(serverId, serverName)))
        {
            return;
        }

        // A fresh page per navigation so each visit starts clean and disposes on Unloaded — the
        // target server is a runtime argument, so this cannot use the type-only NavigateTo cache.
        var page = (IHistoryView)_pageFactory(typeof(HistoryPage));
        _historyFromSidebar = false;
        Show(page, NavigationDestination.History);
        page.Load(serverId, serverName, fromDetail: true);
        _logger.LogInformation("Navigated to History for a server.");
    }

    public void GoToWorkloads(Guid serverId, string serverName)
    {
        _ = Host;
        if (DeferToExitGuard(() => GoToWorkloads(serverId, serverName)))
        {
            return;
        }

        // A fresh page per navigation so each visit starts clean and disposes on Unloaded — the
        // target server is a runtime argument, so this cannot use the type-only NavigateTo cache.
        var page = (WorkloadsPage)_pageFactory(typeof(WorkloadsPage));
        page.Load(serverId, serverName);
        Show(page, NavigationDestination.Workloads);
        _logger.LogInformation("Navigated to Workloads for a server.");
    }

    public void GoToServers()
    {
        _ = Host;

        // Fresh page/VM per visit: the directory holds per-row subscriptions to the shared server cards that must not
        // outlive the visit (released by Show when the page is replaced, and on Unloaded).
        if (CurrentDestination == NavigationDestination.Servers) return;
        if (DeferToExitGuard(GoToServers)) return;
        Show(_pageFactory(typeof(ServersPage)), NavigationDestination.Servers);
        _logger.LogInformation("Navigated to Servers.");
    }

    public void GoToServerDetail(Guid serverId, ServerDetailOrigin origin)
    {
        _ = Host;
        if (DeferToExitGuard(() => GoToServerDetail(serverId, origin)))
        {
            return;
        }

        // Cortex r1 MUST-1: a server that is no longer in the list (removed/hidden between a click or a deep-link and
        // this call) never gets the Detail page — the user lands on the origin instead of a dead page with no way out.
        if (!IsListed(serverId))
        {
            _logger.LogInformation("The Server Detail page was not opened: the server is no longer listed.");
            GoToOrigin(origin);
            return;
        }

        // Content BEFORE Load: if the page has to leave during Load (the server vanished in between), that navigation
        // runs after this one and wins, instead of being overwritten by it.
        _detailOrigins[serverId] = origin;
        _lastDetailServer = serverId;
        var page = (IServerDetailView)_pageFactory(typeof(ServerDetailPage));
        Show(page, NavigationDestination.Detail);
        page.Load(serverId, origin);
        _logger.LogInformation("Navigated to the Server Detail page from {Origin}.", origin);
    }

    public void ReturnToServerDetail(Guid serverId)
    {
        if (serverId == Guid.Empty)
        {
            GoToDashboard();
            return;
        }

        // GoToServerDetail itself falls back to the origin when the server is no longer listed. UI.5 A-2: a listed server
        // without a remembered origin gets "Servidores" as its breadcrumb parent; a gone one still lands on the overview.
        var origin = _detailOrigins.TryGetValue(serverId, out var remembered)
            ? remembered
            : IsListed(serverId) ? ServerDetailOrigin.Servers : ServerDetailOrigin.Overview;
        GoToServerDetail(serverId, origin);
    }

    public void GoToHistory()
    {
        if (CurrentDestination == NavigationDestination.History && _historyFromSidebar) return;
        if (DeferToExitGuard(GoToHistory)) return;
        var page = (IHistoryView)_pageFactory(typeof(HistoryPage));
        _historyFromSidebar = true;
        Show(page, NavigationDestination.History);
        page.LoadSidebar(_lastDetailServer);
    }

    /// <inheritdoc />
    public void GoToServerEditor(ServerEditorRequest request, Action? refused = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = Host;
        if (DeferToExitGuard(() => GoToServerEditor(request, refused), refused))
        {
            return;
        }

        // Content BEFORE Load: replacing the previous page disposes it first (a previous editor's view model is gone before
        // this visit's one is created, CP-13); a fresh page per visit, never cached (B-1).
        var page = (IServerEditorView)_pageFactory(typeof(ServerEditorPage));
        Show(page, NavigationDestination.ServerEditor);
        page.Load(request);
        _logger.LogInformation("Navigated to the server editor ({Mode}).", request.Mode);
    }

    /// <inheritdoc />
    public void LeaveCurrentPageThen(Action continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        if (DeferToExitGuard(continuation))
        {
            return;
        }

        continuation();
    }

    /// <inheritdoc />
    public void LeaveCurrentPageForActivation(Action continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        if (_exitDecisionPending && !_leaving && _host?.Content is INavigationExitGuard)
        {
            _pendingActivation = continuation;
            _logger.LogInformation("An activation arrived while the exit question is open; the latest one is kept.");
            return;
        }

        LeaveCurrentPageThen(continuation);
    }

    /// <summary>
    /// UI.7 H-UI7-3: the ONE exit path. When the page shown is an <see cref="INavigationExitGuard"/>, the navigation runs
    /// only once it says yes - at once for a clean page (a completed answer), after the discard dialog otherwise. True =
    /// this call was taken over (run, deferred or refused); the caller must not navigate itself.
    /// </summary>
    private bool DeferToExitGuard(Action navigation, Action? refused = null)
    {
        if (_leaving || _host?.Content is not INavigationExitGuard guard)
        {
            return false;
        }

        if (_exitDecisionPending)
        {
            // One question at a time: a navigation that arrives while the user decides is dropped, not queued
            // (activations: see _pendingActivation - the latest one runs only if the user discards).
            refused?.Invoke();
            return true;
        }

        Task<bool> decision;
        try
        {
            decision = guard.ConfirmLeaveAsync();
        }
        catch (Exception exception)
        {
            // Fail safe: an exit guard that cannot answer keeps the page (nothing is discarded without an answer).
            _logger.LogError("The exit guard failed. Exception type: {ExceptionType}.", exception.GetType().Name);
            refused?.Invoke();
            return true;
        }

        if (decision.IsCompleted)
        {
            Conclude(decision, navigation, refused);
            return true;
        }

        _exitDecisionPending = true;
        _ = AwaitExitDecisionAsync(decision, navigation, refused);
        return true;
    }

    private async Task AwaitExitDecisionAsync(Task<bool> decision, Action navigation, Action? refused)
    {
        try
        {
            await decision.ConfigureAwait(true);
        }
        catch
        {
            // Observed in Conclude.
        }
        finally
        {
            _exitDecisionPending = false;
        }

        // m-2: an activation that arrived meanwhile replaces the navigation that asked - only if the user discards.
        var activation = _pendingActivation;
        _pendingActivation = null;
        if (activation is not null && decision.Status == TaskStatus.RanToCompletion && decision.Result)
        {
            refused?.Invoke(); // the original navigation will not happen
            Conclude(decision, activation, null, deferred: true);
            return;
        }

        Conclude(decision, navigation, refused, deferred: true);
    }

    private void Conclude(Task<bool> decision, Action navigation, Action? refused, bool deferred = false)
    {
        if (decision.Status != TaskStatus.RanToCompletion || !decision.Result)
        {
            if (decision.IsFaulted)
            {
                _logger.LogError("The exit guard failed. Exception type: {ExceptionType}.",
                    decision.Exception?.InnerException?.GetType().Name ?? nameof(Exception));
            }

            refused?.Invoke();
            return;
        }

        _leaving = true;
        try
        {
            navigation();
        }
        catch (Exception exception) when (deferred)
        {
            // m-1: nobody awaits a deferred navigation. A failure is logged (type only, B-21) and reported to the caller's
            // refusal path, so an editor visit still ends and its opener's command is released. A synchronous navigation
            // keeps throwing to its caller (OpenAsync already ends the visit and reports it).
            _logger.LogError("A deferred navigation failed. Exception type: {ExceptionType}.", exception.GetType().Name);
            refused?.Invoke();
        }
        finally
        {
            _leaving = false;
        }
    }

    private static NavigationDestination DestinationFor(Type type) => type == typeof(DashboardPage)
        ? NavigationDestination.Overview : type == typeof(ServersPage) ? NavigationDestination.Servers
        : type == typeof(ServerDetailPage) ? NavigationDestination.Detail
        : type == typeof(HistoryPage) ? NavigationDestination.History
        : type == typeof(WorkloadsPage) ? NavigationDestination.Workloads
        : type == typeof(SettingsPage) ? NavigationDestination.Settings
        : type == typeof(SettingsDataPage) ? NavigationDestination.SettingsData
        : type == typeof(ServerEditorPage) ? NavigationDestination.ServerEditor
        : throw new ArgumentException("Unknown navigation page.", nameof(type));

    private bool IsListed(Guid serverId) =>
        _serviceProvider.GetService<DashboardViewModel>() is { } dashboard && dashboard.HasVisibleServer(serverId);

    private void GoToOrigin(ServerDetailOrigin origin)
    {
        if (origin == ServerDetailOrigin.Servers)
        {
            GoToServers();
        }
        else
        {
            GoToDashboard();
        }
    }

    private INavigationHost Host => _host ?? throw new InvalidOperationException("Navigation has not been initialized.");

    /// <summary>
    /// Swaps the content and releases the page it replaces when that page owns per-visit subscriptions (Cortex r1
    /// SHOULD-3): a page replaced before it was ever Loaded never gets Unloaded, so Unloaded alone could leak it. The
    /// singleton pages (Visão geral, Definições) are not disposable and are never released here.
    /// </summary>
    private void Show(object page, NavigationDestination destination)
    {
        var previous = Host.Content;
        Host.Content = page;
        CurrentDestination = destination;
        if (!ReferenceEquals(previous, page) && previous is IDisposable disposable)
        {
            disposable.Dispose();
        }

        if (destination != NavigationDestination.Overview)
        {
            NavigatedAwayFromOverview?.Invoke(this, EventArgs.Empty);
        }
        Navigated?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FrameHost(Frame frame) : INavigationHost
    {
        public object? Content
        {
            get => frame.Content;
            set => frame.Content = value;
        }
    }
}

/// <summary>The one thing navigation needs from the shell's Frame (a seam so the real logic is testable).</summary>
internal interface INavigationHost
{
    object? Content { get; set; }
}

/// <summary>The Server Detail page as navigation sees it (D-UI4-DETAIL, UI.5).</summary>
public interface IServerDetailView
{
    void Load(Guid serverId, ServerDetailOrigin origin);
}

/// <summary>UI.7 B-1: the server editor page as navigation sees it (one fresh page per visit).</summary>
public interface IServerEditorView
{
    void Load(ServerEditorRequest request);
}

public interface IHistoryView
{
    void Load(Guid? serverId, string serverName, bool fromDetail);
    void LoadSidebar(Guid? lastDetailServer);
}
