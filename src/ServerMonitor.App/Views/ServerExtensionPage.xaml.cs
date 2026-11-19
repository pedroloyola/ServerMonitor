using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Views;

/// <summary>
/// M16: the shell's page around a contributed server page (<see cref="ServerMonitor.Features.ServerPage"/>). Fresh per
/// visit. The shell draws the way back ("‹ server", to that server's detail page, through navigation like every way back)
/// and the title; the module's view, created for this visit, goes below. The module never navigates.
/// </summary>
public sealed partial class ServerExtensionPage : Page, IServerPageView, IDisposable
{
    private readonly INavigationService _navigation;
    private readonly ILogger<ServerExtensionPage> _logger;
    private Guid _serverId;

    public ServerExtensionPage(INavigationService navigation, ILogger<ServerExtensionPage> logger)
    {
        InitializeComponent();
        _navigation = navigation;
        _logger = logger;
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ShellPageFocus.FocusHeading(this));

        // Released only when navigation replaces this page (it disposes the page it replaces), never on Unloaded: a
        // theme change remounts the page in place, and the module's view must survive that.
    }

    public void Load(Guid serverId, string serverName, ServerPageEntry page, object view)
    {
        ArgumentNullException.ThrowIfNull(page);
        _serverId = serverId;
        Breadcrumb.ParentText = serverName;
        Breadcrumb.CurrentText = page.Title;
        PageTitle.Text = page.Title;
        ModuleContent.Children.Clear();
        try
        {
            ModuleContent.Children.Add((UIElement)view);
        }
        catch (Exception exception)
        {
            // Never fatal: the page keeps its way back, without the module's view.
            _logger.LogError(
                "A contributed server page's view could not be placed. Id: {Id}. Exception type: {ExceptionType}.",
                page.Id, exception.GetType().Name);
        }
    }

    /// <summary>Lets the module's view go with the page (it releases what it holds on its own Unloaded). Idempotent.
    /// Called by navigation when it replaces this page.</summary>
    public void Dispose() => ModuleContent.Children.Clear();

    private void OnBreadcrumbParentInvoked(object? sender, EventArgs e) => _navigation.ReturnToServerDetail(_serverId);
}
