using Windows.System;

namespace ServerMonitor.WidgetProvider.Activation;

/// <summary>
/// Real <see cref="IAppLauncher"/> over the OS protocol launcher. Fire-and-forget: OnActionInvoked must
/// return promptly, so we start the launch and let Windows activate the registered app (which converges
/// on the single UI instance). The URI is always a validated <c>serveralyzer://</c> string.
/// </summary>
internal sealed class ProtocolAppLauncher : IAppLauncher
{
    public void Launch(string uri) => _ = Launcher.LaunchUriAsync(new Uri(uri));
}
