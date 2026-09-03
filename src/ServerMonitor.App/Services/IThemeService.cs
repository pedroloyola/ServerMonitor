using Microsoft.UI.Xaml;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Services;

public interface IThemeService
{
    AppThemePreference Current { get; }

    /// <summary>
    /// Registers a XAML root so it follows the preference. There is more than one root: the tray flyout
    /// is a second one, and <c>RequestedTheme</c> is per-root.
    /// </summary>
    void Attach(FrameworkElement rootElement);

    /// <summary>Unregisters a root that is going away. Attaching without this is a leak.</summary>
    void Detach(FrameworkElement rootElement);

    void Apply(AppThemePreference preference);
}
