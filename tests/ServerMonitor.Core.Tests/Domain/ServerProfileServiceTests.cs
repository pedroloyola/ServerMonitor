using ServerMonitor.Core.Backup;
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
        using var serverService = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());
        var profiles = new ServerProfileService(serverService, credentials, new ConfigurationWriteGate());
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
        using var serverService = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());
        var profiles = new ServerProfileService(serverService, credentials, new ConfigurationWriteGate());
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
        using var serverService = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());
        var profiles = new ServerProfileService(serverService, credentials, new ConfigurationWriteGate());
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
        using var serverService = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());
        var profiles = new ServerProfileService(serverService, credentials, new ConfigurationWriteGate());
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
        using var serverService = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());
        var profiles = new ServerProfileService(serverService, credentials, new ConfigurationWriteGate());
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

    /// <summary>
    /// UI.12 C-E4 (Cortex UI.12A, P2). Retiring the old secret runs AFTER UpdateAsync committed; its failure used to escape
    /// as the operation's failure, so the editor said "changes were not saved" for a saved edit - and a retry from the
    /// stale server could point back at the old reference. Now the edit reports success, the new configuration and secret
    /// stand, the old secret is left as an orphan (never a dangling reference), and the failure is reported sanitized.
    /// </summary>
    [Fact]
    public async Task UpdateReplace_WhenRetiringTheOldSecretFails_TheCommittedEditStillSucceeds()
    {
        var repository = new InMemoryRepository();
        var credentials = new InMemoryCredentialStore();
        var failures = new List<CredentialRetirementFailure>();
        using var serverService = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());
        var profiles = new ServerProfileService(serverService, credentials, new ConfigurationWriteGate(), failures.Add);
        using var initial = new SecretValue("initial".AsSpan());
        var created = (await profiles.AddAsync(new ServerProfileInput
        {
            Configuration = CreatePasswordInput(),
            CredentialChange = CredentialChange.Replace(initial)
        })).Server!;
        using var replacement = new SecretValue("replacement".AsSpan());
        credentials.ThrowOnDelete = true;

        var result = await profiles.UpdateAsync(created, new ServerProfileInput
        {
            Configuration = CreatePasswordInput() with { Name = "Updated" },
            CredentialChange = CredentialChange.Replace(replacement)
        });

        Assert.True(result.Succeeded);
        var saved = Assert.Single(await repository.GetAllAsync());
        Assert.Equal("Updated", saved.Name);
        Assert.Equal(result.Server!.CredentialReferenceId, saved.CredentialReferenceId);
        Assert.NotEqual(created.CredentialReferenceId, saved.CredentialReferenceId);
        Assert.Contains(credentials.Values, pair => pair.Key.ReferenceId == saved.CredentialReferenceId && pair.Value == "replacement");
        Assert.Equal(2, credentials.Values.Count); // the old one is an orphan, not deleted

        var failure = Assert.Single(failures);
        Assert.Equal("update", failure.Operation);
        Assert.Equal(nameof(IOException), failure.ExceptionType);
        Assert.Equal(new IOException().HResult, failure.HResult);
        Assert.DoesNotContain("server.example.test", failure.ToString(), StringComparison.Ordinal);
    }

    /// <summary>UI.12 C-E5bis: the same for Remove - the server is gone, so the removal reports success.</summary>
    [Fact]
    public async Task Remove_WhenRetiringTheSecretFails_TheCommittedRemovalStillSucceeds()
    {
        var repository = new InMemoryRepository();
        var credentials = new InMemoryCredentialStore();
        var failures = new List<CredentialRetirementFailure>();
        using var serverService = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());
        var profiles = new ServerProfileService(serverService, credentials, new ConfigurationWriteGate(), failures.Add);
        using var secret = new SecretValue("initial".AsSpan());
        var created = (await profiles.AddAsync(new ServerProfileInput
        {
            Configuration = CreatePasswordInput(),
            CredentialChange = CredentialChange.Replace(secret)
        })).Server!;
        credentials.ThrowOnDelete = true;

        Assert.True(await profiles.RemoveAsync(created));

        Assert.Empty(await repository.GetAllAsync());
        Assert.Single(credentials.Values); // orphaned, reported
        Assert.Equal("remove", Assert.Single(failures).Operation);
    }

    /// <summary>Without an observer (QA compositions, tests) the committed operation still succeeds.</summary>
    [Fact]
    public async Task AFailedRetirement_WithNoObserver_IsStillASuccess()
    {
        var repository = new InMemoryRepository();
        var credentials = new InMemoryCredentialStore();
        using var serverService = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());
        var profiles = new ServerProfileService(serverService, credentials, new ConfigurationWriteGate());
        using var secret = new SecretValue("initial".AsSpan());
        var created = (await profiles.AddAsync(new ServerProfileInput
        {
            Configuration = CreatePasswordInput(),
            CredentialChange = CredentialChange.Replace(secret)
        })).Server!;
        credentials.ThrowOnDelete = true;

        Assert.True(await profiles.RemoveAsync(created));
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

        /// <summary>UI.12 C-E4: the Credential Manager refuses every delete (the message names a target on purpose).</summary>
        public bool ThrowOnDelete { get; set; }

        public Task<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            ThrowOnDelete
                ? throw new IOException("Credential Manager refused to delete target ServerMonitor/server.example.test.")
                : Task.FromResult(Values.Remove(reference));
    }
}
