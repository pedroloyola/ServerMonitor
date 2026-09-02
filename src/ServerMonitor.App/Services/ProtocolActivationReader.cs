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

    /// <summary>
    /// Classifies WHO asked (M13 S2 §H.2). A redirected plain launch carries its command line, so a
    /// second <c>--background</c> launch is recognizable and must not surface the running instance's UI;
    /// everything else is a person doing something. Classification uses the same strict
    /// <see cref="LaunchModePolicy"/> as startup, so there is exactly one definition of the switch.
    /// Anything unreadable degrades to <see cref="ActivationOrigin.UserActivation"/>, which is the
    /// conservative answer: at worst the app shows itself when asked to, never the reverse.
    /// </summary>
    public static ActivationOrigin ClassifyOrigin(AppActivationArguments? args)
    {
        try
        {
            if (args?.Kind == ExtendedActivationKind.Launch &&
                args.Data is ILaunchActivatedEventArgs launchArgs &&
                LaunchModePolicy.ResolveFromCommandLine(launchArgs.Arguments) == LaunchMode.Background)
            {
                return ActivationOrigin.BackgroundLaunch;
            }
        }
        catch
        {
            // Unreadable activation data: treat it as a user activation.
        }

        return ActivationOrigin.UserActivation;
    }
}
