namespace ServerMonitor.Core.Domain;

public enum ServerValidationErrorCode
{
    NameRequired,
    HostRequired,
    PortOutOfRange,
    UsernameRequired,
    AuthenticationMethodRequired,
    PrivateKeyPathRequired,
    CredentialReferenceRequired,
    CredentialReferenceInvalid,
    ServerNotFound,

    // M14.4b route rules. An invalid route is quarantined, never treated as direct.
    RouteJumpRequired,
    JumpHostRequired,
    JumpPortOutOfRange,
    JumpUsernameRequired,
    JumpAuthenticationMethodRequired,
    JumpPrivateKeyPathRequired,
    JumpCredentialReferenceRequired,
    JumpCredentialReferenceInvalid
}

public sealed record ServerValidationError(
    string PropertyName,
    ServerValidationErrorCode Code);
