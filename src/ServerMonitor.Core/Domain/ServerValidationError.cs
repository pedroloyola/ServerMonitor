namespace ServerMonitor.Core.Domain;

public enum ServerValidationErrorCode
{
    NameRequired,
    HostRequired,
    PortOutOfRange,
    UsernameRequired,
    ServerNotFound
}

public sealed record ServerValidationError(
    string PropertyName,
    ServerValidationErrorCode Code);
