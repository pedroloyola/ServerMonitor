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
        EnsureJumpChangeHasRoute(input);
        var serverId = Guid.NewGuid();
        CredentialReference? stagedReference = null;
        CredentialReference? stagedJumpReference = null;
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
            var jump = await ApplyJumpCredentialChangeAsync(
                serverId,
                configuration,
                input.JumpCredentialChange,
                oldReference: null,
                cancellationToken);
            stagedJumpReference = jump.Staged;
            if (jump.Failure is not null)
            {
                await DeleteAllAsync(stagedReference, stagedJumpReference);
                return jump.Failure;
            }

            var result = await serverService.AddAsync(serverId, jump.Configuration, cancellationToken);
            if (!result.Succeeded)
            {
                await DeleteAllAsync(stagedReference, stagedJumpReference);
            }

            return result;
        }
        catch (ServerPersistencePartialCommitException)
        {
            // The new configuration MAY be on disk and reference the staged secrets: keep them. This fails
            // toward a possible orphan, never toward a server pointing at a deleted secret.
            throw;
        }
        catch
        {
            await DeleteAllAsync(stagedReference, stagedJumpReference);
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
        EnsureJumpChangeHasRoute(input);

        var oldReference = CreateReference(existingServer);
        var oldJumpReference = CreateJumpReference(existingServer);
        CredentialReference? stagedReference = null;
        CredentialReference? stagedJumpReference = null;
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
            var jump = await ApplyJumpCredentialChangeAsync(
                existingServer.Id,
                configuration,
                input.JumpCredentialChange,
                oldJumpReference,
                cancellationToken);
            stagedJumpReference = jump.Staged;
            if (jump.Failure is not null)
            {
                await DeleteAllAsync(stagedReference, stagedJumpReference);
                return jump.Failure;
            }

            result = await serverService.UpdateAsync(existingServer.Id, jump.Configuration, cancellationToken);
        }
        catch (ServerPersistencePartialCommitException)
        {
            // The new configuration MAY be on disk and reference the staged secrets: keep them. This fails
            // toward a possible orphan, never toward a server pointing at a deleted secret.
            throw;
        }
        catch
        {
            await DeleteAllAsync(stagedReference, stagedJumpReference);
            throw;
        }

        if (!result.Succeeded)
        {
            await DeleteAllAsync(stagedReference, stagedJumpReference);
            return result;
        }

        var retiredReference = oldReference is not null
            && (stagedReference is not null || input.CredentialChange.Mode == CredentialChangeMode.Clear)
                ? oldReference
                : null;

        // The old jump secret is retired whenever the saved route no longer points at it: replaced,
        // cleared, re-kinded, or the route itself removed. Otherwise it would be orphaned.
        var retiredJumpReference = oldJumpReference is not null
            && oldJumpReference != CreateJumpReference(result.Server!)
                ? oldJumpReference
                : null;

        await DeleteAllAsync(retiredReference, retiredJumpReference);
        return result;
    }

    public async Task<bool> RemoveAsync(Server server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        // A failed save (rolled back, or partial) throws here, before any secret is touched: the server may
        // still be on disk, so its secrets must survive.
        var removed = await serverService.RemoveAsync(server.Id, cancellationToken);
        if (!removed)
        {
            return false;
        }

        await DeleteAllAsync(CreateReference(server), CreateJumpReference(server));
        return true;
    }

    private async Task<JumpCredentialOutcome> ApplyJumpCredentialChangeAsync(
        Guid serverId,
        ServerInput configuration,
        CredentialChange? change,
        CredentialReference? oldReference,
        CancellationToken cancellationToken)
    {
        if (configuration.Route?.Jump is not { } jump)
        {
            return new(configuration, null, null);
        }

        var kind = GetJumpCredentialKind(jump.AuthenticationMethod);
        CredentialReference? staged = null;
        Guid? referenceId;
        switch (change?.Mode)
        {
            case CredentialChangeMode.Replace:
                var secret = change.Secret
                    ?? throw new ArgumentException("A replacement jump secret is required.", nameof(change));
                staged = CredentialReference.Create(
                    serverId,
                    kind ?? throw new ArgumentException("Jump authentication must be configured.", nameof(change)));
                await credentialStore.WriteAsync(staged.Value, secret, cancellationToken);
                referenceId = staged.Value.ReferenceId;
                break;

            case CredentialChangeMode.Keep:
                if (oldReference is null || oldReference.Value.Kind != kind)
                {
                    return new(configuration, null, JumpCredentialRequired());
                }

                referenceId = oldReference.Value.ReferenceId;
                break;

            case CredentialChangeMode.Clear:
                if (jump.AuthenticationMethod == AuthenticationMethod.Password)
                {
                    return new(configuration, null, JumpCredentialRequired());
                }

                referenceId = null;
                break;

            case null:
                if (oldReference is not null && oldReference.Value.Kind == kind)
                {
                    referenceId = oldReference.Value.ReferenceId;
                }
                else if (jump.AuthenticationMethod == AuthenticationMethod.Password)
                {
                    return new(configuration, null, JumpCredentialRequired());
                }
                else
                {
                    referenceId = null;
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }

        var routed = configuration with
        {
            Route = configuration.Route with { Jump = jump with { CredentialReferenceId = referenceId } }
        };
        return new(routed, staged, null);
    }

    private static void EnsureJumpChangeHasRoute(ServerProfileInput input)
    {
        if (input.JumpCredentialChange?.Mode == CredentialChangeMode.Replace
            && input.Configuration.Route?.Jump is null)
        {
            throw new ArgumentException("A jump credential requires a route with a jump host.", nameof(input));
        }
    }

    /// <summary>
    /// Deletes every given reference, attempting all of them even when one fails, so a failure on one
    /// secret never leaves the other orphaned. The first failure is rethrown after every attempt.
    /// </summary>
    private async Task DeleteAllAsync(params CredentialReference?[] references)
    {
        Exception? firstFailure = null;
        foreach (var reference in references)
        {
            if (reference is null)
            {
                continue;
            }

            try
            {
                await credentialStore.DeleteAsync(reference.Value, CancellationToken.None);
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
            }
        }

        if (firstFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(firstFailure);
        }
    }

    private static ServerOperationResult JumpCredentialRequired() =>
        ServerOperationResult.Failure(new ServerValidationError(
            nameof(JumpHop.CredentialReferenceId),
            ServerValidationErrorCode.JumpCredentialReferenceRequired));

    private static CredentialReference? CreateReference(Server server)
    {
        if (server.CredentialReferenceId is not Guid referenceId)
        {
            return null;
        }

        return new CredentialReference(server.Id, GetCredentialKind(server.AuthenticationMethod), referenceId);
    }

    private static CredentialReference? CreateJumpReference(Server server)
    {
        if (server.Route?.Jump is not { CredentialReferenceId: Guid referenceId } jump
            || GetJumpCredentialKind(jump.AuthenticationMethod) is not { } kind)
        {
            return null;
        }

        return new CredentialReference(server.Id, kind, referenceId);
    }

    private static ServerCredentialKind GetCredentialKind(AuthenticationMethod authenticationMethod) =>
        authenticationMethod switch
        {
            AuthenticationMethod.Password => ServerCredentialKind.Password,
            AuthenticationMethod.SshKey => ServerCredentialKind.PrivateKeyPassphrase,
            _ => throw new ArgumentException("Authentication must be configured.", nameof(authenticationMethod))
        };

    private static ServerCredentialKind? GetJumpCredentialKind(AuthenticationMethod authenticationMethod) =>
        authenticationMethod switch
        {
            AuthenticationMethod.Password => ServerCredentialKind.JumpPassword,
            AuthenticationMethod.SshKey => ServerCredentialKind.JumpPrivateKeyPassphrase,
            _ => null
        };

    private sealed record JumpCredentialOutcome(
        ServerInput Configuration,
        CredentialReference? Staged,
        ServerOperationResult? Failure);
}
