using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Alerts;

/// <summary>
/// Pure M8 transition policy. Initial observations and transitions involving Unknown are
/// baselines rather than alerts; repeated states and partial recovery from Critical to
/// Warning are also silent.
/// </summary>
public static class ServerAlertPolicy
{
    public static ServerAlertDecision? Evaluate(ServerHealth previous, ServerHealth current)
    {
        if (previous == current || previous == ServerHealth.Unknown || current == ServerHealth.Unknown)
        {
            return null;
        }

        if (current == ServerHealth.Offline)
        {
            return new(ServerAlertCategory.Offline, previous, current);
        }

        if (previous == ServerHealth.Offline)
        {
            return new(ServerAlertCategory.Recovery, previous, current);
        }

        if (current == ServerHealth.Critical &&
            previous is ServerHealth.Healthy or ServerHealth.Warning)
        {
            return new(ServerAlertCategory.Critical, previous, current);
        }

        if (previous == ServerHealth.Healthy && current == ServerHealth.Warning)
        {
            return new(ServerAlertCategory.Warning, previous, current);
        }

        if (current == ServerHealth.Healthy &&
            previous is ServerHealth.Warning or ServerHealth.Critical)
        {
            return new(ServerAlertCategory.Recovery, previous, current);
        }

        return null;
    }
}
