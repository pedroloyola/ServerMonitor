namespace ServerMonitor.Core.Enums;

/// <summary>
/// Which hop presented the host key carried by an <see cref="Models.SshConnectionResult"/>. It decides the ONLY
/// store a user-confirmed key may be written to: <see cref="Direct"/> and <see cref="Jump"/> go to the direct
/// store (under the server's / the jump host's own endpoint), <see cref="Target"/> goes to the routed store
/// under the <see cref="Models.SshRoute"/>. A target key is never written to the direct store and a jump key
/// is never written to the routed store.
/// </summary>
public enum SshHostKeyHop
{
    /// <summary>A direct (non-routed) server.</summary>
    Direct,

    /// <summary>The jump host of a routed server, verified against the direct store by its own endpoint.</summary>
    Jump,

    /// <summary>The target of a routed server, seen through the jump, verified by its <see cref="Models.SshRoute"/>.</summary>
    Target
}
