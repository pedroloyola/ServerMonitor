using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.Infrastructure.Tests.Persistence;

/// <summary>
/// M14.4b-1 review F1: a two-file save is all-or-nothing from the caller's view, and the credential
/// lifecycle stays consistent with what is actually on disk. Uses the REAL repository, server service and
/// profile service; only the Windows credential store is in memory.
/// </summary>
public sealed class ServerSaveAtomicityTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ServerMonitor.SaveAtomicity.Tests",
        Guid.NewGuid().ToString("N"));

    private string DirectPath => Path.Combine(_directory, "servers.json");

    private string RoutedPath => Path.Combine(_directory, "routed-servers.json");

    [Fact]
    public async Task GainingARoute_WhenTheSecondFileFails_RollsBackTheFirst_AndOnlyTheStagedSecretsAreDeleted()
    {
        var credentials = new InMemoryCredentialStore();
        var (profiles, service) = Compose(CreateRepository(), credentials);
        var created = await AddDirectAsync(profiles);
        var t1 = created.CredentialReferenceId;

        using var targetSecret = Secret("t2");
        using var jumpSecret = Secret("j2");
        using (Block(DirectPath))
        {
            var failure = await Assert.ThrowsAnyAsync<IOException>(() => profiles.UpdateAsync(created, new ServerProfileInput
            {
                Configuration = PasswordInput() with { Route = PasswordRoute() },
                CredentialChange = CredentialChange.Replace(targetSecret),
                JumpCredentialChange = CredentialChange.Replace(jumpSecret)
            }));
            Assert.IsNotType<ServerPersistencePartialCommitException>(failure);
        }

        // The routed file written first was rolled back (it did not exist before).
        Assert.False(File.Exists(RoutedPath));
        var reloaded = Assert.Single(await CreateRepository().GetAllAsync());
        Assert.Null(reloaded.Route);
        Assert.Equal(t1, reloaded.CredentialReferenceId);
        // T1 survives; the staged T2/J2 were deleted because nothing referencing them was persisted.
        Assert.Equal("t1", Assert.Single(credentials.Values).Value);
        AssertEveryReferenceExists(reloaded, credentials);
        service.Dispose();
    }

    [Fact]
    public async Task RemovingARoutedServer_WhenTheSecondFileFails_KeepsTheServerAndItsSecrets()
    {
        var credentials = new InMemoryCredentialStore();
        var (profiles, service) = Compose(CreateRepository(), credentials);
        var created = await AddRoutedAsync(profiles);
        var routedBefore = await File.ReadAllBytesAsync(RoutedPath);

        using (Block(DirectPath))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => profiles.RemoveAsync(created));
        }

        Assert.Equal(routedBefore, await File.ReadAllBytesAsync(RoutedPath));
        var reloaded = Assert.Single(await CreateRepository().GetAllAsync());
        Assert.Equal(created.Route, reloaded.Route);
        Assert.Equal(2, credentials.Values.Count);
        AssertEveryReferenceExists(reloaded, credentials);
        service.Dispose();
    }

    [Fact]
    public async Task WhenTheRollbackAlsoFails_APartialCommitIsReported_AndNoSecretTheDiskMayReferenceIsDeleted()
    {
        var credentials = new InMemoryCredentialStore();
        var repository = CreateRepository(point =>
        {
            if (point == "write:" + DirectPath || point == "restore:" + RoutedPath)
            {
                throw new IOException("Synthetic I/O failure at " + Path.GetFileName(point));
            }
        });
        var (profiles, service) = Compose(repository, credentials);
        var created = await AddDirectWithoutFaultsAsync(credentials);

        using var targetSecret = Secret("t2");
        using var jumpSecret = Secret("j2");
        await Assert.ThrowsAsync<ServerPersistencePartialCommitException>(() => profiles.UpdateAsync(created, new ServerProfileInput
        {
            Configuration = PasswordInput() with { Route = PasswordRoute() },
            CredentialChange = CredentialChange.Replace(targetSecret),
            JumpCredentialChange = CredentialChange.Replace(jumpSecret)
        }));

        // The routed copy (new state) stayed on disk and wins on load: everything it points at must exist.
        var reloaded = Assert.Single(await CreateRepository().GetAllAsync());
        Assert.NotNull(reloaded.Route);
        AssertEveryReferenceExists(reloaded, credentials);
        // Nothing was deleted: T1 (possibly orphaned now), T2 and J2 are all still there.
        Assert.Equal(3, credentials.Values.Count);
        service.Dispose();
    }

    [Fact]
    public async Task ExistingRoutedFile_IsRestoredByteForByte_WhenTheLegacyWriteFails()
    {
        var credentials = new InMemoryCredentialStore();
        var (profiles, service) = Compose(CreateRepository(), credentials);
        var created = await AddRoutedAsync(profiles);
        var routedBefore = await File.ReadAllBytesAsync(RoutedPath);
        var directBefore = await File.ReadAllBytesAsync(DirectPath);

        using (Block(DirectPath))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => profiles.UpdateAsync(created, new ServerProfileInput
            {
                Configuration = PasswordInput() with { Name = "renamed", Route = created.Route },
                CredentialChange = CredentialChange.Keep
            }));
        }

        Assert.Equal(routedBefore, await File.ReadAllBytesAsync(RoutedPath));
        Assert.Equal(directBefore, await File.ReadAllBytesAsync(DirectPath));
        service.Dispose();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static void AssertEveryReferenceExists(Server server, InMemoryCredentialStore credentials)
    {
        if (server.CredentialReferenceId is { } target)
        {
            Assert.Contains(credentials.Values.Keys, reference =>
                reference.ServerId == server.Id && reference.ReferenceId == target);
        }

        if (server.Route?.Jump?.CredentialReferenceId is { } jump)
        {
            Assert.Contains(credentials.Values.Keys, reference =>
                reference.ServerId == server.Id && reference.ReferenceId == jump);
        }
    }

    private JsonServerRepository CreateRepository(Action<string>? faults = null) => new(
        new ServerStorageOptions { FilePath = DirectPath },
        NullLogger<JsonServerRepository>.Instance)
    {
        FaultInjector = faults
    };

    private static (ServerProfileService Profiles, ServerService Service) Compose(
        IServerRepository repository,
        IServerCredentialStore credentials)
    {
        var service = new ServerService(repository, new ServerValidator());
        return (new ServerProfileService(service, credentials), service);
    }

    private static async Task<Server> AddDirectAsync(ServerProfileService profiles)
    {
        using var secret = Secret("t1");
        var result = await profiles.AddAsync(new ServerProfileInput
        {
            Configuration = PasswordInput(),
            CredentialChange = CredentialChange.Replace(secret)
        });
        Assert.True(result.Succeeded);
        return result.Server!;
    }

    // Seeds the files through a fault-free repository instance; the faulty one then loads them.
    private async Task<Server> AddDirectWithoutFaultsAsync(InMemoryCredentialStore credentials)
    {
        var (profiles, service) = Compose(CreateRepository(), credentials);
        using (service)
        {
            return await AddDirectAsync(profiles);
        }
    }

    private static async Task<Server> AddRoutedAsync(ServerProfileService profiles)
    {
        using var target = Secret("t1");
        using var jump = Secret("j1");
        var result = await profiles.AddAsync(new ServerProfileInput
        {
            Configuration = PasswordInput() with { Route = PasswordRoute() },
            CredentialChange = CredentialChange.Replace(target),
            JumpCredentialChange = CredentialChange.Replace(jump)
        });
        Assert.True(result.Succeeded);
        return result.Server!;
    }

    // Holds the writer's temporary open exclusively, so that file's atomic write fails.
    private static FileStream Block(string path) =>
        new(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None);

    private static SecretValue Secret(string value) => new(value.AsSpan());

    private static ServerInput PasswordInput() => new()
    {
        Name = "Server",
        Host = "10.0.0.5",
        Port = 22,
        Username = "monitor",
        AuthenticationMethod = AuthenticationMethod.Password
    };

    private static ServerRoute PasswordRoute() => new()
    {
        Jump = new JumpHop
        {
            Host = "bastion.example.test",
            Username = "jump",
            AuthenticationMethod = AuthenticationMethod.Password
        }
    };

    private sealed class InMemoryCredentialStore : IServerCredentialStore
    {
        public Dictionary<CredentialReference, string> Values { get; } = [];

        public Task WriteAsync(CredentialReference reference, SecretValue secret, CancellationToken cancellationToken = default)
        {
            Values[reference] = new string(secret.Reveal());
            return Task.CompletedTask;
        }

        public Task<SecretValue?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryGetValue(reference, out var value) ? new SecretValue(value.AsSpan()) : null);

        public Task<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.Remove(reference));
    }
}
