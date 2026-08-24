using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Domain;

public sealed class ServerValidator : IServerValidator
{
    public ServerValidationResult Validate(ServerInput input) => ValidateInput(input, true);

    public ServerValidationResult ValidateDraft(ServerInput input) => ValidateInput(input, false);

    public ServerValidationResult Validate(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var input = new ServerInput
        {
            Name = server.Name,
            Host = server.Host,
            Port = server.Port,
            Username = server.Username,
            OperatingSystem = server.OperatingSystem,
            AuthenticationMethod = server.AuthenticationMethod,
            PrivateKeyPath = server.PrivateKeyPath,
            CredentialReferenceId = server.CredentialReferenceId
        };

        return server.AuthenticationMethod == Enums.AuthenticationMethod.NotConfigured
            ? ValidateCommon(input)
            : ValidateInput(input, true);
    }

    private static ServerValidationResult ValidateInput(ServerInput input, bool requireCredentialReference)
    {
        ArgumentNullException.ThrowIfNull(input);

        var errors = ValidateCommon(input).Errors.ToList();

        if (input.AuthenticationMethod == Enums.AuthenticationMethod.NotConfigured
            || !Enum.IsDefined(input.AuthenticationMethod))
        {
            errors.Add(new(nameof(input.AuthenticationMethod), ServerValidationErrorCode.AuthenticationMethodRequired));
        }

        if (input.AuthenticationMethod == Enums.AuthenticationMethod.SshKey
            && string.IsNullOrWhiteSpace(input.PrivateKeyPath))
        {
            errors.Add(new(nameof(input.PrivateKeyPath), ServerValidationErrorCode.PrivateKeyPathRequired));
        }

        if (requireCredentialReference
            && input.AuthenticationMethod == Enums.AuthenticationMethod.Password
            && input.CredentialReferenceId is null)
        {
            errors.Add(new(nameof(input.CredentialReferenceId), ServerValidationErrorCode.CredentialReferenceRequired));
        }

        if (input.CredentialReferenceId == Guid.Empty)
        {
            errors.Add(new(nameof(input.CredentialReferenceId), ServerValidationErrorCode.CredentialReferenceInvalid));
        }

        return errors.Count == 0
            ? ServerValidationResult.Success
            : new ServerValidationResult(errors);
    }

    private static ServerValidationResult ValidateCommon(ServerInput input)
    {
        var errors = new List<ServerValidationError>();

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            errors.Add(new(nameof(input.Name), ServerValidationErrorCode.NameRequired));
        }

        if (string.IsNullOrWhiteSpace(input.Host))
        {
            errors.Add(new(nameof(input.Host), ServerValidationErrorCode.HostRequired));
        }

        if (input.Port is < 1 or > 65535)
        {
            errors.Add(new(nameof(input.Port), ServerValidationErrorCode.PortOutOfRange));
        }

        if (string.IsNullOrWhiteSpace(input.Username))
        {
            errors.Add(new(nameof(input.Username), ServerValidationErrorCode.UsernameRequired));
        }

        return errors.Count == 0
            ? ServerValidationResult.Success
            : new ServerValidationResult(errors);
    }
}
