using ServerMonitor.Core.Security;

namespace ServerMonitor.Core.Models;

public sealed record ServerProfileInput
{
    public required ServerInput Configuration { get; init; }

    public required CredentialChange CredentialChange { get; init; }

    /// <summary>
    /// The change to the route's jump-host secret, independent of <see cref="CredentialChange"/>.
    /// <see langword="null"/> means "no change requested": an existing jump reference of the right kind is
    /// kept, and a route without one stays without one (a password jump then fails validation).
    /// </summary>
    public CredentialChange? JumpCredentialChange { get; init; }

    public override string ToString() => "ServerProfileInput { Configuration = [NON-SENSITIVE], CredentialChange = [REDACTED], JumpCredentialChange = [REDACTED] }";
}
