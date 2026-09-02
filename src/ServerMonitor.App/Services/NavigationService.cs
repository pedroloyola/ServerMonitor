using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Views;

namespace ServerMonitor.App.Services;

public sealed class NavigationService(
    IServiceProvider serviceProvider,
    ILogger<NavigationService> logger) : INavigationService
{
    private Frame? _frame;

    public void Initialize(Frame frame) => _frame = frame;

    public void NavigateTo<TPage>() where TPage : Page
    {
        if (_frame is null)
        {
            throw new InvalidOperationException("Navigation has not been initialized.");
        }

        if (_frame.Content is TPage)
        {
            return;
        }

        _frame.Content = serviceProvider.GetRequiredService<TPage>();
        logger.LogInformation("Navigated to {Page}.", typeof(TPage).Name);
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
        if (_frame is null)
        {
            throw new InvalidOperationException("Navigation has not been initialized.");
        }

        // A fresh page per navigation so each visit starts clean and disposes on Unloaded — the
        // target server is a runtime argument, so this cannot use the type-only NavigateTo cache.
        var page = serviceProvider.GetRequiredService<HistoryPage>();
        page.Load(serverId, serverName);
        _frame.Content = page;
        logger.LogInformation("Navigated to History for a server.");
    }

    public void GoToWorkloads(Guid serverId, string serverName)
    {
        if (_frame is null)
        {
            throw new InvalidOperationException("Navigation has not been initialized.");
        }

        // A fresh page per navigation so each visit starts clean and disposes on Unloaded — the
        // target server is a runtime argument, so this cannot use the type-only NavigateTo cache.
        var page = serviceProvider.GetRequiredService<WorkloadsPage>();
        page.Load(serverId, serverName);
        _frame.Content = page;
        logger.LogInformation("Navigated to Workloads for a server.");
    }
}
