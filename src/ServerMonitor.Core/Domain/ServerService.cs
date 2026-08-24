using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Domain;

public sealed class ServerService(
    IServerRepository repository,
    IServerValidator validator) : IServerService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<Server>? _servers;

    public event EventHandler? ServersChanged;

    public async Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return _servers!.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServerOperationResult> AddAsync(
        ServerInput input,
        CancellationToken cancellationToken = default) =>
        await AddAsync(Guid.NewGuid(), input, cancellationToken);

    public async Task<ServerOperationResult> AddAsync(
        Guid id,
        ServerInput input,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("The server id cannot be empty.", nameof(id));
        }

        var normalized = Normalize(input);
        var validation = validator.Validate(normalized);
        if (!validation.IsValid)
        {
            return new ServerOperationResult(null, validation);
        }

        var server = new Server
        {
            Id = id,
            Name = normalized.Name,
            Host = normalized.Host,
            Port = normalized.Port,
            Username = normalized.Username,
            OperatingSystem = normalized.OperatingSystem,
            AuthenticationMethod = normalized.AuthenticationMethod,
            PrivateKeyPath = normalized.PrivateKeyPath,
            CredentialReferenceId = normalized.CredentialReferenceId,
            IsHidden = false,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await MutateAsync(servers => [.. servers, server], cancellationToken);
        return new ServerOperationResult(server, ServerValidationResult.Success);
    }

    public async Task<ServerOperationResult> UpdateAsync(
        Guid id,
        ServerInput input,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(input);
        var validation = validator.Validate(normalized);
        if (!validation.IsValid)
        {
            return new ServerOperationResult(null, validation);
        }

        Server? updated = null;
        var changed = await MutateAsync(servers =>
        {
            var index = servers.FindIndex(server => server.Id == id);
            if (index < 0)
            {
                return null;
            }

            updated = servers[index] with
            {
                Name = normalized.Name,
                Host = normalized.Host,
                Port = normalized.Port,
                Username = normalized.Username,
                OperatingSystem = normalized.OperatingSystem,
                AuthenticationMethod = normalized.AuthenticationMethod,
                PrivateKeyPath = normalized.PrivateKeyPath,
                CredentialReferenceId = normalized.CredentialReferenceId
            };

            var copy = servers.ToList();
            copy[index] = updated;
            return copy;
        }, cancellationToken);

        return changed && updated is not null
            ? new ServerOperationResult(updated, ServerValidationResult.Success)
            : ServerOperationResult.Failure(
                new ServerValidationError(
                    nameof(Server.Id),
                    ServerValidationErrorCode.ServerNotFound));
    }

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) =>
        MutateAsync(servers =>
        {
            var copy = servers.Where(server => server.Id != id).ToList();
            return copy.Count == servers.Count ? null : copy;
        }, cancellationToken);

    public Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default) =>
        SetHiddenAsync(id, true, cancellationToken);

    public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default) =>
        SetHiddenAsync(id, false, cancellationToken);

    public void Dispose() => _gate.Dispose();

    private Task<bool> SetHiddenAsync(
        Guid id,
        bool isHidden,
        CancellationToken cancellationToken) =>
        MutateAsync(servers =>
        {
            var index = servers.FindIndex(server => server.Id == id);
            if (index < 0 || servers[index].IsHidden == isHidden)
            {
                return null;
            }

            var copy = servers.ToList();
            copy[index] = copy[index] with { IsHidden = isHidden };
            return copy;
        }, cancellationToken);

    private async Task<bool> MutateAsync(
        Func<List<Server>, List<Server>?> mutation,
        CancellationToken cancellationToken)
    {
        var changed = false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            var candidate = mutation(_servers!);
            if (candidate is null)
            {
                return false;
            }

            await repository.SaveAllAsync(candidate, cancellationToken);
            _servers = candidate;
            changed = true;
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            ServersChanged?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_servers is not null)
        {
            return;
        }

        var persisted = await repository.GetAllAsync(cancellationToken);
        _servers = persisted.Where(server => validator.Validate(server).IsValid).ToList();
    }

    private static ServerInput Normalize(ServerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return input with
        {
            Name = (input.Name ?? string.Empty).Trim(),
            Host = (input.Host ?? string.Empty).Trim(),
            Username = (input.Username ?? string.Empty).Trim(),
            PrivateKeyPath = string.IsNullOrWhiteSpace(input.PrivateKeyPath)
                ? null
                : Path.GetFullPath(input.PrivateKeyPath.Trim())
        };
    }
}
