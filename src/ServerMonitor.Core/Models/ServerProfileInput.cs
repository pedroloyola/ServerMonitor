using ServerMonitor.Core.Security;

namespace ServerMonitor.Core.Models;

public sealed record ServerProfileInput
{
    public required ServerInput Configuration { get; init; }

    public required CredentialChange CredentialChange { get; init; }

    public override string ToString() => "ServerProfileInput { Configuration = [NON-SENSITIVE], CredentialChange = [REDACTED] }";
}
