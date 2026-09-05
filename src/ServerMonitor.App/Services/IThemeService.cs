using Microsoft.UI.Xaml;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Services;

public interface IThemeService
{
    AppThemePreference Current { get; }

    /// <summary>
    /// Registers a XAML root so it follows the preference, because <c>RequestedTheme</c> is per-root.
    /// </summary>
    /// <remarks>
    /// The tray menu used to be a second root. It is now a native shell menu, which takes its theme from
    /// <see cref="Current"/> through uxtheme instead of from a <c>FrameworkElement</c>.
    /// </remarks>
    void Attach(FrameworkElement rootElement);

    /// <summary>Unregisters a root that is going away. Attaching without this is a leak.</summary>
    void Detach(FrameworkElement rootElement);

    void Apply(AppThemePreference preference);
}
