using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;

namespace ServerMonitor.Core.Tests.Domain;

public sealed class ServerProfileServiceTests
{
    [Fact]
    public async Task AddPassword_StagesCredentialAndPersistsOpaqueReference()
    {
        var repository = new InMemoryRepository();
        var credentials = new InMemoryCredentialStore();
        using var serverService = new ServerService(repository, new ServerValidator());
        var profiles = new ServerProfileService(serverService, credentials);
        using var secret = new SecretValue("correct horse".AsSpan());

        var result = await profiles.AddAsync(new ServerProfileInput
        {
            Configuration = CreatePasswordInput(),
            CredentialChange = CredentialChange.Replace(secret)
        });

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Server!.CredentialReferenceId);
        Assert.Single(credentials.Values);
        Assert.DoesNotContain("correct horse", System.Text.Json.JsonSerializer.Serialize(result.Server));
    }

    [Fact]
    public async Task Add_WhenPersistenceFails_RemovesStagedCredential()
    {
        var repository = new InMemoryRepository { ThrowOnSave = true };
        var credentials = new InMemoryCredentialStore();
        using var serverService = new ServerService(repository, new ServerValidator());
        var profiles = new ServerProfileService(serverService, credentials);
        using var secret = new SecretValue("temporary".AsSpan());

        await Assert.ThrowsAsync<IOException>(() => profiles.AddAsync(new ServerProfileInput
        {
            Configuration = CreatePasswordInput(),
            CredentialChange = CredentialChange.Replace(secret)
        }));

        Assert.Empty(credentials.Values);
    }

    [Fact]
    public async Task UpdateKeep_PreservesCredentialReference()
    {
        var repository = new InMemoryRepository();
        var credentials = new InMemoryCredentialStore();
        using var serverService = new ServerService(repository, new ServerValidator());
        var profiles = new ServerProfileService(serverService, credentials);
        using var secret = new SecretValue("initial".AsSpan());
        var created = (await profiles.AddAsync(new ServerProfileInput
        {
            Configuration = CreatePasswordInput(),
            CredentialChange = CredentialChange.Replace(secret)
        })).Server!;

        var result = await profiles.UpdateAsync(created, new ServerProfileInput
        {
            Configuration = CreatePasswordInput() with { Name = "Updated" },
            CredentialChange = CredentialChange.Keep
        });

        Assert.True(result.Succeeded);
        Assert.Equal(created.CredentialReferenceId, result.Server!.CredentialReferenceId);
        Assert.Single(credentials.Values);
    }

    [Fact]
    public async Task UpdateReplace_RotatesReferenceAfterPersistingConfiguration()
    {
        var repository = new InMemoryRepository();
        var credentials = new InMemoryCredentialStore();
        using var serverService = new ServerService(repository, new ServerValidator());
        var profiles = new ServerProfileService(serverService, credentials);
        using var initial = new SecretValue("initial".AsSpan());
        var created = (await profiles.AddAsync(new ServerProfileInput
        {
            Configuration = CreatePasswordInput(),
            CredentialChange = CredentialChange.Replace(initial)
        })).Server!;
        using var replacement = new SecretValue("replacement".AsSpan());

        var result = await profiles.UpdateAsync(created, new ServerProfileInput
        {
            Configuration = CreatePasswordInput(),
            CredentialChange = CredentialChange.Replace(replacement)
        });

        Assert.True(result.Succeeded);
        Assert.NotEqual(created.CredentialReferenceId, result.Server!.CredentialReferenceId);
        Assert.Single(credentials.Values);
    }

    [Fact]
    public async Task Remove_DeletesConfigurationThenCredential()
    {
        var repository = new InMemoryRepository();
        var credentials = new InMemoryCredentialStore();
        using var serverService = new ServerService(repository, new ServerValidator());
        var profiles = new ServerProfileService(serverService, credentials);
        using var secret = new SecretValue("initial".AsSpan());
        var created = (await profiles.AddAsync(new ServerProfileInput
        {
            Configuration = CreatePasswordInput(),
            CredentialChange = CredentialChange.Replace(secret)
        })).Server!;

        Assert.True(await profiles.RemoveAsync(created));
        Assert.Empty(await repository.GetAllAsync());
        Assert.Empty(credentials.Values);
    }

    private static ServerInput CreatePasswordInput() => new()
    {
        Name = "Test server",
        Host = "server.example.test",
        Port = 22,
        Username = "monitor",
        OperatingSystem = ServerOperatingSystem.Auto,
        AuthenticationMethod = AuthenticationMethod.Password
    };

    private sealed class InMemoryRepository : IServerRepository
    {
        private List<Server> _servers = [];

        public bool ThrowOnSave { get; init; }

        public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Server>>(_servers.ToArray());

        public Task SaveAllAsync(IReadOnlyCollection<Server> servers, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSave)
            {
                throw new IOException("Synthetic persistence failure.");
            }

            _servers = servers.ToList();
            return Task.CompletedTask;
        }
    }

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
