using Microsoft.UI.Xaml;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Services;

public interface IThemeService
{
    AppThemePreference Current { get; }

    void Attach(FrameworkElement rootElement);

    void Apply(AppThemePreference preference);
}
