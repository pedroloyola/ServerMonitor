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
    JumpCredentialReferenceInvalid,

    // UI.7 H-UI7-1: format rules for NEW writes only (ValidateDraft and Validate(ServerInput), i.e. the editor and the
    // Add/Update path of ServerService). Validate(Server) - load/quarantine and backup restore - never applies them, so
    // an existing server or backup that breaks one still loads/restores; it shows the error only when edited.
    HostFormatInvalid,
    PrivateKeyIsPublicKey,
    JumpPrivateKeyIsPublicKey,
    JumpEndpointIsTarget
}

public sealed record ServerValidationError(
    string PropertyName,
    ServerValidationErrorCode Code);
