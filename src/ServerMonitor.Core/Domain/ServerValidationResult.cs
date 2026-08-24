namespace ServerMonitor.Core.Domain;

public sealed record ServerValidationResult(IReadOnlyList<ServerValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static ServerValidationResult Success { get; } = new([]);
}
