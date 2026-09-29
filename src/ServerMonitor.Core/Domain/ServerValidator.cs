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
            CredentialReferenceId = server.CredentialReferenceId,
            RefreshIntervalSeconds = server.RefreshIntervalSeconds,
            Route = server.Route
        };

        if (server.AuthenticationMethod != Enums.AuthenticationMethod.NotConfigured)
        {
            return ValidateInput(input, true);
        }

        // A migrated (M2) target may still lack authentication, but its route never gets that latitude.
        var errors = ValidateCommon(input).Errors.Concat(ValidateRoute(input.Route, true)).ToList();
        return errors.Count == 0
            ? ServerValidationResult.Success
            : new ServerValidationResult(errors);
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

        errors.AddRange(ValidateRoute(input.Route, requireCredentialReference));

        return errors.Count == 0
            ? ServerValidationResult.Success
            : new ServerValidationResult(errors);
    }

    private static IEnumerable<ServerValidationError> ValidateRoute(ServerRoute? route, bool requireCredentialReference)
    {
        if (route is null)
        {
            yield break;
        }

        if (route.Jump is not { } jump)
        {
            yield return new(nameof(ServerInput.Route), ServerValidationErrorCode.RouteJumpRequired);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(jump.Host))
        {
            yield return new(nameof(JumpHop.Host), ServerValidationErrorCode.JumpHostRequired);
        }

        if (jump.Port is < 1 or > 65535)
        {
            yield return new(nameof(JumpHop.Port), ServerValidationErrorCode.JumpPortOutOfRange);
        }

        if (string.IsNullOrWhiteSpace(jump.Username))
        {
            yield return new(nameof(JumpHop.Username), ServerValidationErrorCode.JumpUsernameRequired);
        }

        // Unlike a migrated target, a jump host always needs a real authentication method.
        if (jump.AuthenticationMethod == Enums.AuthenticationMethod.NotConfigured
            || !Enum.IsDefined(jump.AuthenticationMethod))
        {
            yield return new(nameof(JumpHop.AuthenticationMethod), ServerValidationErrorCode.JumpAuthenticationMethodRequired);
        }

        if (jump.AuthenticationMethod == Enums.AuthenticationMethod.SshKey
            && string.IsNullOrWhiteSpace(jump.PrivateKeyPath))
        {
            yield return new(nameof(JumpHop.PrivateKeyPath), ServerValidationErrorCode.JumpPrivateKeyPathRequired);
        }

        if (requireCredentialReference
            && jump.AuthenticationMethod == Enums.AuthenticationMethod.Password
            && jump.CredentialReferenceId is null)
        {
            yield return new(nameof(JumpHop.CredentialReferenceId), ServerValidationErrorCode.JumpCredentialReferenceRequired);
        }

        if (jump.CredentialReferenceId == Guid.Empty)
        {
            yield return new(nameof(JumpHop.CredentialReferenceId), ServerValidationErrorCode.JumpCredentialReferenceInvalid);
        }
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
