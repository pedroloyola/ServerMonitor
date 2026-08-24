using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Domain;

public sealed record ServerOperationResult(
    Server? Server,
    ServerValidationResult Validation)
{
    public bool Succeeded => Server is not null && Validation.IsValid;

    public static ServerOperationResult Failure(params ServerValidationError[] errors) =>
        new(null, new ServerValidationResult(errors));
}
