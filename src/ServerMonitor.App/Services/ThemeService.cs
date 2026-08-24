using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Services;

public sealed class ThemeService(ILogger<ThemeService> logger) : IThemeService
{
    private FrameworkElement? _rootElement;

    public AppThemePreference Current { get; private set; } = AppThemePreference.System;

    public void Attach(FrameworkElement rootElement)
    {
        _rootElement = rootElement;
        ApplyToRoot();
    }

    public void Apply(AppThemePreference preference)
    {
        Current = preference;
        ApplyToRoot();
        logger.LogInformation("Application theme changed to {Theme}.", preference);
    }

    private void ApplyToRoot()
    {
        if (_rootElement is null)
        {
            return;
        }

        _rootElement.RequestedTheme = Current switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }
}
