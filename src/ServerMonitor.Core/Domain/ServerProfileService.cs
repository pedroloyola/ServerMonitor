using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;

namespace ServerMonitor.Core.Domain;

public sealed class ServerProfileService(
    IServerService serverService,
    IServerCredentialStore credentialStore) : IServerProfileService
{
    public async Task<ServerOperationResult> AddAsync(
        ServerProfileInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var serverId = Guid.NewGuid();
        CredentialReference? stagedReference = null;
        var configuration = input.Configuration;

        if (input.CredentialChange.Mode == CredentialChangeMode.Replace)
        {
            var secret = input.CredentialChange.Secret
                ?? throw new ArgumentException("A replacement secret is required.", nameof(input));
            stagedReference = CredentialReference.Create(serverId, GetCredentialKind(configuration.AuthenticationMethod));
            await credentialStore.WriteAsync(stagedReference.Value, secret, cancellationToken);
            configuration = configuration with { CredentialReferenceId = stagedReference.Value.ReferenceId };
        }
        else if (configuration.AuthenticationMethod == AuthenticationMethod.Password)
        {
            return ServerOperationResult.Failure(new ServerValidationError(
                nameof(ServerInput.CredentialReferenceId),
                ServerValidationErrorCode.CredentialReferenceRequired));
        }
        else
        {
            configuration = configuration with { CredentialReferenceId = null };
        }

        try
        {
            var result = await serverService.AddAsync(serverId, configuration, cancellationToken);
            if (!result.Succeeded && stagedReference is not null)
            {
                await credentialStore.DeleteAsync(stagedReference.Value, CancellationToken.None);
            }

            return result;
        }
        catch
        {
            if (stagedReference is not null)
            {
                await credentialStore.DeleteAsync(stagedReference.Value, CancellationToken.None);
            }

            throw;
        }
    }

    public async Task<ServerOperationResult> UpdateAsync(
        Server existingServer,
        ServerProfileInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existingServer);
        ArgumentNullException.ThrowIfNull(input);

        var oldReference = CreateReference(existingServer);
        CredentialReference? stagedReference = null;
        var configuration = input.Configuration;

        switch (input.CredentialChange.Mode)
        {
            case CredentialChangeMode.Keep:
                if (oldReference is null
                    || oldReference.Value.Kind != GetCredentialKind(configuration.AuthenticationMethod))
                {
                    return ServerOperationResult.Failure(new ServerValidationError(
                        nameof(ServerInput.CredentialReferenceId),
                        ServerValidationErrorCode.CredentialReferenceRequired));
                }

                configuration = configuration with { CredentialReferenceId = oldReference.Value.ReferenceId };
                break;

            case CredentialChangeMode.Replace:
                var secret = input.CredentialChange.Secret
                    ?? throw new ArgumentException("A replacement secret is required.", nameof(input));
                stagedReference = CredentialReference.Create(
                    existingServer.Id,
                    GetCredentialKind(configuration.AuthenticationMethod));
                await credentialStore.WriteAsync(stagedReference.Value, secret, cancellationToken);
                configuration = configuration with { CredentialReferenceId = stagedReference.Value.ReferenceId };
                break;

            case CredentialChangeMode.Clear:
                if (configuration.AuthenticationMethod == AuthenticationMethod.Password)
                {
                    return ServerOperationResult.Failure(new ServerValidationError(
                        nameof(ServerInput.CredentialReferenceId),
                        ServerValidationErrorCode.CredentialReferenceRequired));
                }

                configuration = configuration with { CredentialReferenceId = null };
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(input));
        }

        ServerOperationResult result;
        try
        {
            result = await serverService.UpdateAsync(existingServer.Id, configuration, cancellationToken);
        }
        catch
        {
            if (stagedReference is not null)
            {
                await credentialStore.DeleteAsync(stagedReference.Value, CancellationToken.None);
            }

            throw;
        }

        if (!result.Succeeded)
        {
            if (stagedReference is not null)
            {
                await credentialStore.DeleteAsync(stagedReference.Value, CancellationToken.None);
            }

            return result;
        }

        if (oldReference is not null
            && (stagedReference is not null || input.CredentialChange.Mode == CredentialChangeMode.Clear))
        {
            await credentialStore.DeleteAsync(oldReference.Value, CancellationToken.None);
        }

        return result;
    }

    public async Task<bool> RemoveAsync(Server server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        var removed = await serverService.RemoveAsync(server.Id, cancellationToken);
        if (!removed)
        {
            return false;
        }

        var reference = CreateReference(server);
        if (reference is not null)
        {
            await credentialStore.DeleteAsync(reference.Value, CancellationToken.None);
        }

        return true;
    }

    private static CredentialReference? CreateReference(Server server)
    {
        if (server.CredentialReferenceId is not Guid referenceId)
        {
            return null;
        }

        return new CredentialReference(server.Id, GetCredentialKind(server.AuthenticationMethod), referenceId);
    }

    private static ServerCredentialKind GetCredentialKind(AuthenticationMethod authenticationMethod) =>
        authenticationMethod switch
        {
            AuthenticationMethod.Password => ServerCredentialKind.Password,
            AuthenticationMethod.SshKey => ServerCredentialKind.PrivateKeyPassphrase,
            _ => throw new ArgumentException("Authentication must be configured.", nameof(authenticationMethod))
        };
}
