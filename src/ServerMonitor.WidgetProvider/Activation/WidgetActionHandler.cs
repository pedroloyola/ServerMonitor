using System.Text.Json;
using ServerMonitor.ActivationContract;
using ServerMonitor.WidgetProvider.Diagnostics;

namespace ServerMonitor.WidgetProvider.Activation;

/// <summary>
/// Turns an untrusted widget <c>Action.Execute</c> (verb + data) into a validated app launch (§14). The
/// provider NEVER navigates UI, reads servers.json, or resolves credentials — it only maps an allowlisted
/// verb + opaque server id to a strict <c>serveralyzer://</c> URI and launches it; the app does the rest.
/// Every failure is contained (neutral-on-exception, §16/§31): an unknown verb, malformed data, or a
/// launch error is logged coarsely (never the raw payload) and dropped — the widget keeps working.
/// </summary>
public sealed class WidgetActionHandler
{
    private readonly IAppLauncher _launcher;
    private readonly IWidgetProviderLog _log;

    public WidgetActionHandler(IAppLauncher launcher, IWidgetProviderLog? log = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _log = log ?? NullWidgetProviderLog.Instance;
    }

    /// <summary>Handles one invoked action. <paramref name="dataJson"/> is the action's untrusted data.</summary>
    public void Handle(string? verb, string? dataJson)
    {
        try
        {
            Guid? serverId = null;
            if (string.Equals(verb, ActivationVerbs.OpenServer, StringComparison.Ordinal))
            {
                serverId = TryReadServerId(dataJson);
            }

            var intent = ActivationVerbs.TryToIntent(verb, serverId);
            if (intent is null)
            {
                _log.Warn("Widget action ignored: unrecognized verb or missing id.");
                return;
            }

            _launcher.Launch(ActivationUri.Format(intent));
        }
        catch (Exception exception)
        {
            _log.Warn($"Widget action failed. Error: {exception.GetType().Name}.");
        }
    }

    // Reads only the opaque serverId guid from the action data. Any other field is ignored; malformed
    // JSON or a bad guid yields null (which makes an openServer verb a no-op rather than a bad launch).
    private static Guid? TryReadServerId(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(dataJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(ActivationVerbs.ServerIdDataKey, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                Guid.TryParseExact(value.GetString(), "D", out var id))
            {
                return id;
            }
        }
        catch (JsonException)
        {
            // Untrusted, malformed data — ignore.
        }

        return null;
    }
}
