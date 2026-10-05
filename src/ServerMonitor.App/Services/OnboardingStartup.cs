namespace ServerMonitor.App.Services;

public static class OnboardingStartup
{
    /// <summary>Capture before initial navigation populates the host.</summary>
    public static bool IsNormalStart(NavigationDestination? destination, LaunchMode mode) =>
        destination is null && mode == LaunchMode.Foreground;
}
