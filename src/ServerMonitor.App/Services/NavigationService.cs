using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;

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
}
