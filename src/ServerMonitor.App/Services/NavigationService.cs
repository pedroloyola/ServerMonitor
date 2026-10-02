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
        if (Host.Content is TPage)
        {
            return;
        }

        Show(_pageFactory(typeof(TPage)), isOverview: typeof(TPage) == typeof(DashboardPage));
        _logger.LogInformation("Navigated to {Page}.", typeof(TPage).Name);
    }

    public void GoToDashboard() => NavigateTo<DashboardPage>();

    public void GoToSettings() => NavigateTo<SettingsPage>();

    private int _backgroundSettingsFocusRequested;

    public void RequestBackgroundSettingsFocus() =>
        Interlocked.Exchange(ref _backgroundSettingsFocusRequested, 1);

    public bool ConsumeBackgroundSettingsFocus() =>
        Interlocked.Exchange(ref _backgroundSettingsFocusRequested, 0) == 1;

    public void GoToHistory(Guid serverId, string serverName)
    {
        _ = Host;

        // A fresh page per navigation so each visit starts clean and disposes on Unloaded — the
        // target server is a runtime argument, so this cannot use the type-only NavigateTo cache.
        var page = (HistoryPage)_pageFactory(typeof(HistoryPage));
        page.Load(serverId, serverName);
        Show(page, isOverview: false);
        _logger.LogInformation("Navigated to History for a server.");
    }

    public void GoToWorkloads(Guid serverId, string serverName)
    {
        _ = Host;

        // A fresh page per navigation so each visit starts clean and disposes on Unloaded — the
        // target server is a runtime argument, so this cannot use the type-only NavigateTo cache.
        var page = (WorkloadsPage)_pageFactory(typeof(WorkloadsPage));
        page.Load(serverId, serverName);
        Show(page, isOverview: false);
        _logger.LogInformation("Navigated to Workloads for a server.");
    }

    public void GoToServers()
    {
        _ = Host;

        // Fresh page/VM per visit: the directory holds per-row subscriptions to the shared server cards that must not
        // outlive the visit (released by Show when the page is replaced, and on Unloaded).
        Show(_pageFactory(typeof(ServersPage)), isOverview: false);
        _logger.LogInformation("Navigated to Servers.");
    }

    public void GoToServerDetail(Guid serverId, ServerDetailOrigin origin)
    {
        _ = Host;

        // Cortex r1 MUST-1: a server that is no longer in the list (removed/hidden between a click or a deep-link and
        // this call) never gets the interim page — the user lands on the origin instead of a dead page with no way out.
        if (_serviceProvider.GetService<DashboardViewModel>() is not { } dashboard || !dashboard.HasVisibleServer(serverId))
        {
            _logger.LogInformation("The interim server page was not opened: the server is no longer listed.");
            GoToOrigin(origin);
            return;
        }

        // Content BEFORE Load: if the page has to leave during Load (the server vanished in between), that navigation
        // runs after this one and wins, instead of being overwritten by it.
        _detailOrigins[serverId] = origin;
        var page = (IServerDetailView)_pageFactory(typeof(ServerDetailPage));
        Show(page, isOverview: false);
        page.Load(serverId, origin);
        _logger.LogInformation("Navigated to the interim server page from {Origin}.", origin);
    }

    public void ReturnToServerDetail(Guid serverId)
    {
        if (serverId == Guid.Empty)
        {
            GoToDashboard();
            return;
        }

        // GoToServerDetail itself falls back to the origin when the server is no longer listed.
        GoToServerDetail(serverId, _detailOrigins.GetValueOrDefault(serverId, ServerDetailOrigin.Overview));
    }

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
    private void Show(object page, bool isOverview)
    {
        var previous = Host.Content;
        Host.Content = page;
        if (!ReferenceEquals(previous, page) && previous is IDisposable disposable)
        {
            disposable.Dispose();
        }

        if (!isOverview)
        {
            NavigatedAwayFromOverview?.Invoke(this, EventArgs.Empty);
        }
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

/// <summary>The interim server page as navigation sees it (D-UI4-DETAIL).</summary>
public interface IServerDetailView
{
    void Load(Guid serverId, ServerDetailOrigin origin);
}
