using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;

namespace ServerMonitor.Core.Tests.Backup;

public sealed class RestoreRekeyerTests
{
    [Fact]
    public void EveryImpliedReference_GetsAFreshId_AndKeepsItsSecret()
    {
        var target = Guid.NewGuid();
        var jump = Guid.NewGuid();
        var server = Routed(target, jump);
        using var targetSecret = new SecretValue("t");
        using var jumpSecret = new SecretValue("j");
        var credentials = new[]
        {
            new BackupCredential(server.Id, ServerCredentialKind.Password, target, targetSecret),
            new BackupCredential(server.Id, ServerCredentialKind.JumpPassword, jump, jumpSecret),
        };

        var result = RestoreRekeyer.Rekey([server], credentials, Guid.NewGuid);

        var rekeyed = Assert.Single(result.Servers);
        Assert.NotEqual(target, rekeyed.CredentialReferenceId);
        Assert.NotEqual(jump, rekeyed.Route!.Jump!.CredentialReferenceId);
        Assert.Equal(2, result.Credentials.Count);
        Assert.Contains(result.Credentials, c => c.Reference.ReferenceId == rekeyed.CredentialReferenceId && ReferenceEquals(c.Secret, targetSecret));
        Assert.Contains(result.Credentials, c => c.Reference.ReferenceId == rekeyed.Route.Jump.CredentialReferenceId && ReferenceEquals(c.Secret, jumpSecret));
        Assert.Empty(result.ReferencesWithoutSecret);
        Assert.Equal(server with { CredentialReferenceId = rekeyed.CredentialReferenceId, Route = rekeyed.Route }, rekeyed);
    }

    [Fact]
    public void MissingCredential_GetsAFreshIdWithoutSecret()
    {
        var server = Direct(Guid.NewGuid());

        var result = RestoreRekeyer.Rekey([server], [], Guid.NewGuid);

        var reference = Assert.Single(result.ReferencesWithoutSecret);
        Assert.Equal(result.Servers[0].CredentialReferenceId, reference.ReferenceId);
        Assert.NotEqual(server.CredentialReferenceId, reference.ReferenceId);
        Assert.Empty(result.Credentials);
    }

    [Fact]
    public void ReferenceWithoutASecretKind_IsCleared()
    {
        var server = Direct(Guid.NewGuid()) with { AuthenticationMethod = AuthenticationMethod.NotConfigured };

        var result = RestoreRekeyer.Rekey([server], [], Guid.NewGuid);

        Assert.Null(result.Servers[0].CredentialReferenceId);
        Assert.Empty(result.ReferencesWithoutSecret);
    }

    [Fact]
    public void AnIdGeneratorThatReusesTheOriginalId_IsRefused()
    {
        var original = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() => RestoreRekeyer.Rekey([Direct(original)], [], () => original));
        Assert.Throws<InvalidOperationException>(() => RestoreRekeyer.Rekey([Direct(original)], [], () => Guid.Empty));
    }

    internal static Server Direct(Guid referenceId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "d",
        Host = "h",
        Username = "u",
        AuthenticationMethod = AuthenticationMethod.Password,
        CredentialReferenceId = referenceId
    };

    private static Server Routed(Guid target, Guid jump) => Direct(target) with
    {
        Route = new ServerRoute
        {
            Jump = new JumpHop
            {
                Host = "b",
                Username = "j",
                AuthenticationMethod = AuthenticationMethod.Password,
                CredentialReferenceId = jump
            }
        }
    };
}

// M14.6 §5.5: the domain writers refuse to write while a restore holds the gate (checked inside their locks).
public sealed class GatedDomainWritersTests
{
    [Fact]
    public async Task ServerService_EveryMutation_IsRefusedWhileLocked_AndNothingIsSaved()
    {
        var repository = new CountingRepository();
        var gate = new ConfigurationWriteGate();
        using var service = new ServerService(repository, new ServerValidator(), gate);
        var existing = (await service.AddAsync(Input())).Server!;
        var saves = repository.Saves;

        var token = await gate.BeginRestoreAsync(TimeSpan.FromSeconds(1));
        Assert.NotNull(token);

        await Assert.ThrowsAsync<ConfigurationLockedException>(() => service.AddAsync(Input()));
        await Assert.ThrowsAsync<ConfigurationLockedException>(() => service.UpdateAsync(existing.Id, Input()));
        await Assert.ThrowsAsync<ConfigurationLockedException>(() => service.RemoveAsync(existing.Id));
        await Assert.ThrowsAsync<ConfigurationLockedException>(() => service.HideAsync(existing.Id));
        Assert.Equal(saves, repository.Saves);

        gate.Release(token);
        Assert.True(await service.HideAsync(existing.Id));
    }

    [Fact]
    public async Task ServerService_AfterSeal_StaysRefused()
    {
        var repository = new CountingRepository();
        var gate = new ConfigurationWriteGate();
        using var service = new ServerService(repository, new ServerValidator(), gate);
        var token = (await gate.BeginRestoreAsync(TimeSpan.FromSeconds(1)))!;

        gate.Seal(token);

        await Assert.ThrowsAsync<ConfigurationLockedException>(() => service.AddAsync(Input()));
        Assert.Equal(0, repository.Saves);
    }

    [Fact]
    public async Task ServerProfileService_IsRefusedBeforeAnyCredentialWrite()
    {
        var repository = new CountingRepository();
        var gate = new ConfigurationWriteGate();
        using var service = new ServerService(repository, new ServerValidator(), gate);
        var credentials = new CountingCredentialStore();
        var profiles = new ServerProfileService(service, credentials, gate);
        var token = (await gate.BeginRestoreAsync(TimeSpan.FromSeconds(1)))!;
        using var secret = new SecretValue("s");

        await Assert.ThrowsAsync<ConfigurationLockedException>(() => profiles.AddAsync(new ServerProfileInput
        {
            Configuration = Input(),
            CredentialChange = new CredentialChange { Mode = CredentialChangeMode.Replace, Secret = secret }
        }));
        await Assert.ThrowsAsync<ConfigurationLockedException>(() => profiles.RemoveAsync(new Server { Id = Guid.NewGuid() }));

        Assert.Equal(0, credentials.Writes);
        Assert.Equal(0, credentials.Deletes);
        Assert.Equal(0, repository.Saves);
        gate.Release(token);
    }

    private static ServerInput Input() => new()
    {
        Name = "s",
        Host = "h",
        Username = "u",
        AuthenticationMethod = AuthenticationMethod.Password,
        CredentialReferenceId = Guid.NewGuid()
    };

    private sealed class CountingRepository : IServerRepository
    {
        private IReadOnlyList<Server> _servers = [];

        public int Saves { get; private set; }

        public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(_servers);

        public Task SaveAllAsync(IReadOnlyCollection<Server> servers, CancellationToken cancellationToken = default)
        {
            Saves++;
            _servers = servers.ToList();
            return Task.CompletedTask;
        }
    }

    private sealed class CountingCredentialStore : IServerCredentialStore
    {
        public int Writes { get; private set; }

        public int Deletes { get; private set; }

        public Task WriteAsync(CredentialReference reference, SecretValue secret, CancellationToken cancellationToken = default)
        {
            Writes++;
            return Task.CompletedTask;
        }

        public Task<SecretValue?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult<SecretValue?>(null);

        public Task<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default)
        {
            Deletes++;
            return Task.FromResult(true);
        }
    }
}
