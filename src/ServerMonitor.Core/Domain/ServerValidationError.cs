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
    ServerNotFound
}

public sealed record ServerValidationError(
    string PropertyName,
    ServerValidationErrorCode Code);
