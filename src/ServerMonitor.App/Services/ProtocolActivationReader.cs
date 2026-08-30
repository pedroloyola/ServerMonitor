using Microsoft.Windows.AppLifecycle;
using ServerMonitor.ActivationContract;
using Windows.ApplicationModel.Activation;

namespace ServerMonitor.App.Services;

/// <summary>
/// Extracts a validated <see cref="ActivationIntent"/> from a Windows activation, treating the URI as
/// untrusted (§10). Returns <c>null</c> for a normal launch, a non-protocol activation, or any malformed/
/// unrecognized <c>serveralyzer://</c> URI — the app then behaves as a normal launch (§22/§32). Never
/// throws.
/// </summary>
public static class ProtocolActivationReader
{
    public static ActivationIntent? TryGetIntent(AppActivationArguments? args)
    {
        try
        {
            if (args?.Kind == ExtendedActivationKind.Protocol &&
                args.Data is IProtocolActivatedEventArgs protocolArgs)
            {
                return ActivationUri.TryParse(protocolArgs.Uri?.ToString());
            }
        }
        catch
        {
            // Untrusted/unexpected activation data — fall back to a normal launch.
        }

        return null;
    }
}
