namespace ServerMonitor.Core.Models;

/// <summary>
/// The trust key of a target reached THROUGH a jump host: the jump endpoint (<see cref="Via"/>) and the
/// target endpoint as the jump sees it. The same private address behind two bastions is two routes.
/// </summary>
public sealed record SshRoute(SshEndpoint Via, SshEndpoint Target)
{
    public static SshRoute Create(SshEndpoint via, SshEndpoint target)
    {
        ArgumentNullException.ThrowIfNull(via);
        ArgumentNullException.ThrowIfNull(target);

        return new SshRoute(
            SshEndpoint.Create(via.Host, via.Port),
            SshEndpoint.Create(target.Host, target.Port));
    }

    public override string ToString() => $"{Target} via {Via}";
}
