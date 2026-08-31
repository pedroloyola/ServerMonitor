namespace ServerMonitor.WidgetProvider.Activation;

/// <summary>
/// Launches a validated <c>serveralyzer://</c> deep-link so the app is activated (§14). Abstracted so the
/// action handler is unit-testable without the real OS launcher. The only input is a URI already produced
/// by <c>ActivationUri.Format</c> from an allowlisted intent — never free/user text.
/// </summary>
public interface IAppLauncher
{
    void Launch(string uri);
}
