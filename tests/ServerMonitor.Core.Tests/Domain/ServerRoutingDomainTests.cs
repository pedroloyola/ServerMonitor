using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;

namespace ServerMonitor.Core.Tests.Domain;

/// <summary>M14.4b-1: service quarantine, route validation and jump credentials.</summary>
public sealed class ServerRoutingDomainTests
{
    private static readonly string KeyPath = Path.Combine(Path.GetTempPath(), "id_test");

    // ---- ServerService quarantine --------------------------------------------------------------------

    [Fact]
    public async Task InvalidPersistedServer_IsHidden_ButWrittenBackOnEverySave()
    {
        var invalid = PersistedServer() with { Name = " " };
        var repository = new InMemoryRepository([PersistedServer(), invalid]);
        using var service = new ServerService(repository, new ServerValidator());

        var visible = await service.GetAllAsync();
        Assert.DoesNotContain(visible, server => server.Id == invalid.Id);

        await service.AddAsync(DirectInput());
        Assert.Contains(invalid, repository.Saved);

        await service.RemoveAsync(visible.Single().Id);
        Assert.Contains(invalid, repository.Saved);
        Assert.DoesNotContain(await service.GetAllAsync(), server => server.Id == invalid.Id);
    }

    [Fact]
    public async Task InvalidRoute_IsQuarantined_NeverExposedAsDirect()
    {
        var brokenRoute = PersistedServer() with { Route = new ServerRoute { Jump = null } };
        var badJump = PersistedServer() with { Route = new ServerRoute { Jump = Jump() with { Host = "" } } };
        var repository = new InMemoryRepository([brokenRoute, badJump]);
        using var service = new ServerService(repository, new ServerValidator());

        Assert.Empty(await service.GetAllAsync());

        await service.AddAsync(DirectInput());
        Assert.Contains(repository.Saved, server => server.Id == brokenRoute.Id && server.Route == brokenRoute.Route);
        Assert.Contains(repository.Saved, server => server.Id == badJump.Id && server.Route == badJump.Route);
    }

    [Fact]
    public async Task AddAndUpdate_CarryTheRoute_AndNormalizeTheJump()
    {
        var repository = new InMemoryRepository([]);
        using var service = new ServerService(repository, new ServerValidator());

        var added = await service.AddAsync(DirectInput() with
        {
            Route = new ServerRoute { Jump = Jump() with { Host = "  bastion.example.test ", Username = " jump " } }
        });
        Assert.True(added.Succeeded);
        Assert.Equal("bastion.example.test", added.Server!.Route!.Jump!.Host);
        Assert.Equal("jump", added.Server.Route.Jump.Username);

        var updated = await service.UpdateAsync(added.Server.Id, DirectInput() with
        {
            Name = "renamed",
            Route = added.Server.Route
        });
        Assert.Equal(added.Server.Route, updated.Server!.Route);
        Assert.Equal(added.Server.Route, Assert.Single(repository.Saved).Route);
    }

    // ---- ServerValidator route rules -----------------------------------------------------------------

    [Theory]
    [InlineData(ServerValidationErrorCode.JumpHostRequired)]
    [InlineData(ServerValidationErrorCode.JumpPortOutOfRange)]
    [InlineData(ServerValidationErrorCode.JumpUsernameRequired)]
    [InlineData(ServerValidationErrorCode.JumpAuthenticationMethodRequired)]
    [InlineData(ServerValidationErrorCode.JumpPrivateKeyPathRequired)]
    [InlineData(ServerValidationErrorCode.JumpCredentialReferenceRequired)]
    [InlineData(ServerValidationErrorCode.JumpCredentialReferenceInvalid)]
    [InlineData(ServerValidationErrorCode.RouteJumpRequired)]
    public void RouteRules_ApplyToInputsAndPersistedServers_EvenWhenTheTargetIsUnconfigured(ServerValidationErrorCode expected)
    {
        var route = BrokenRoute(expected);
        var validator = new ServerValidator();

        Assert.Contains(validator.Validate(DirectInput() with { Route = route }).Errors, error => error.Code == expected);
        Assert.Contains(validator.Validate(PersistedServer() with { Route = route }).Errors, error => error.Code == expected);
        // A migrated M2 target may lack authentication; its route never gets that latitude.
        Assert.Contains(
            validator.Validate(PersistedServer() with
            {
                AuthenticationMethod = AuthenticationMethod.NotConfigured,
                PrivateKeyPath = null,
                Route = route
            }).Errors,
            error => error.Code == expected);
    }

    [Fact]
    public void ValidRoute_Passes_AndDraftDoesNotRequireAJumpReference()
    {
        var validator = new ServerValidator();

        Assert.True(validator.Validate(DirectInput() with { Route = new ServerRoute { Jump = Jump() } }).IsValid);
        Assert.True(validator.ValidateDraft(DirectInput() with
        {
            Route = new ServerRoute { Jump = Jump() with { AuthenticationMethod = AuthenticationMethod.Password } }
        }).IsValid);
    }

    // ---- ServerProfileService jump credentials -------------------------------------------------------

    [Fact]
    public async Task Add_StagesAnIndependentJumpCredential_WithTheJumpKind()
    {
        var fixture = new ProfileFixture();
        using var target = Secret("target");
        using var jump = Secret("jump");

        var result = await fixture.Profiles.AddAsync(new ServerProfileInput
        {
            Configuration = PasswordInput() with { Route = PasswordRoute() },
            CredentialChange = CredentialChange.Replace(target),
            JumpCredentialChange = CredentialChange.Replace(jump)
        });

        Assert.True(result.Succeeded);
        var server = result.Server!;
        var jumpReference = new CredentialReference(server.Id, ServerCredentialKind.JumpPassword, server.Route!.Jump!.CredentialReferenceId!.Value);
        var targetReference = new CredentialReference(server.Id, ServerCredentialKind.Password, server.CredentialReferenceId!.Value);
        Assert.NotEqual(server.CredentialReferenceId, server.Route.Jump.CredentialReferenceId);
        Assert.Equal("jump", fixture.Credentials.Values[jumpReference]);
        Assert.Equal("target", fixture.Credentials.Values[targetReference]);
        Assert.Equal(2, fixture.Credentials.Values.Count);
    }

    [Fact]
    public async Task Add_WhenPersistenceFails_RemovesBothStagedSecrets()
    {
        var fixture = new ProfileFixture(throwOnSave: true);
        using var target = Secret("target");
        using var jump = Secret("jump");

        await Assert.ThrowsAsync<IOException>(() => fixture.Profiles.AddAsync(new ServerProfileInput
        {
            Configuration = PasswordInput() with { Route = PasswordRoute() },
            CredentialChange = CredentialChange.Replace(target),
            JumpCredentialChange = CredentialChange.Replace(jump)
        }));

        Assert.Empty(fixture.Credentials.Values);
    }

    [Fact]
    public async Task Add_WhenJumpStagingFails_RemovesTheStagedTargetSecret()
    {
        var fixture = new ProfileFixture();
        fixture.Credentials.FailWritesOf = ServerCredentialKind.JumpPassword;
        using var target = Secret("target");
        using var jump = Secret("jump");

        await Assert.ThrowsAsync<IOException>(() => fixture.Profiles.AddAsync(new ServerProfileInput
        {
            Configuration = PasswordInput() with { Route = PasswordRoute() },
            CredentialChange = CredentialChange.Replace(target),
            JumpCredentialChange = CredentialChange.Replace(jump)
        }));

        Assert.Empty(fixture.Credentials.Values);
        Assert.Empty(fixture.Repository.Saved);
    }

    [Fact]
    public async Task Add_PasswordJumpWithoutSecret_FailsAndLeavesNoSecret()
    {
        var fixture = new ProfileFixture();
        using var target = Secret("target");

        var result = await fixture.Profiles.AddAsync(new ServerProfileInput
        {
            Configuration = PasswordInput() with { Route = PasswordRoute() },
            CredentialChange = CredentialChange.Replace(target)
        });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Validation.Errors, error => error.Code == ServerValidationErrorCode.JumpCredentialReferenceRequired);
        Assert.Empty(fixture.Credentials.Values);
    }

    [Fact]
    public async Task Update_WithoutAJumpChange_KeepsTheJumpSecret()
    {
        var fixture = new ProfileFixture();
        var created = await fixture.CreateRoutedAsync();

        var result = await fixture.Profiles.UpdateAsync(created, new ServerProfileInput
        {
            Configuration = PasswordInput() with { Name = "renamed", Route = created.Route },
            CredentialChange = CredentialChange.Keep
        });

        Assert.True(result.Succeeded);
        Assert.Equal(created.Route!.Jump!.CredentialReferenceId, result.Server!.Route!.Jump!.CredentialReferenceId);
        Assert.Equal(2, fixture.Credentials.Values.Count);
    }

    [Fact]
    public async Task Update_ReplacingTheJumpSecret_RotatesItAndDeletesTheOldOne()
    {
        var fixture = new ProfileFixture();
        var created = await fixture.CreateRoutedAsync();
        using var replacement = Secret("jump-2");

        var result = await fixture.Profiles.UpdateAsync(created, new ServerProfileInput
        {
            Configuration = PasswordInput() with { Route = created.Route },
            CredentialChange = CredentialChange.Keep,
            JumpCredentialChange = CredentialChange.Replace(replacement)
        });

        Assert.True(result.Succeeded);
        Assert.NotEqual(created.Route!.Jump!.CredentialReferenceId, result.Server!.Route!.Jump!.CredentialReferenceId);
        Assert.Equal(2, fixture.Credentials.Values.Count);
        Assert.Contains("jump-2", fixture.Credentials.Values.Values);
        Assert.DoesNotContain("jump", fixture.Credentials.Values.Values);
        Assert.Equal(created.CredentialReferenceId, result.Server.CredentialReferenceId);
    }

    [Fact]
    public async Task Update_RemovingTheRoute_DeletesTheJumpSecret()
    {
        var fixture = new ProfileFixture();
        var created = await fixture.CreateRoutedAsync();

        var result = await fixture.Profiles.UpdateAsync(created, new ServerProfileInput
        {
            Configuration = PasswordInput() with { Route = null },
            CredentialChange = CredentialChange.Keep
        });

        Assert.True(result.Succeeded);
        Assert.Null(result.Server!.Route);
        Assert.Equal("target", Assert.Single(fixture.Credentials.Values).Value);
    }

    [Fact]
    public async Task Update_WhenPersistenceFails_RemovesBothStagedSecretsAndKeepsTheOldOnes()
    {
        var fixture = new ProfileFixture();
        var created = await fixture.CreateRoutedAsync();
        var before = fixture.Credentials.Values.ToDictionary();
        fixture.Repository.ThrowOnSave = true;
        using var target = Secret("target-2");
        using var jump = Secret("jump-2");

        await Assert.ThrowsAsync<IOException>(() => fixture.Profiles.UpdateAsync(created, new ServerProfileInput
        {
            Configuration = PasswordInput() with { Route = created.Route },
            CredentialChange = CredentialChange.Replace(target),
            JumpCredentialChange = CredentialChange.Replace(jump)
        }));

        Assert.Equal(before, fixture.Credentials.Values);
    }

    [Fact]
    public async Task Remove_DeletesTargetAndJumpSecrets()
    {
        var fixture = new ProfileFixture();
        var created = await fixture.CreateRoutedAsync();

        Assert.True(await fixture.Profiles.RemoveAsync(created));

        Assert.Empty(fixture.Credentials.Values);
    }

    [Fact]
    public async Task Remove_WhenTheTargetDeleteFails_StillDeletesTheJumpSecret()
    {
        var fixture = new ProfileFixture();
        var created = await fixture.CreateRoutedAsync();
        fixture.Credentials.FailDeletesOf = ServerCredentialKind.Password;

        await Assert.ThrowsAsync<IOException>(() => fixture.Profiles.RemoveAsync(created));

        Assert.DoesNotContain(fixture.Credentials.Values.Keys, reference => reference.Kind == ServerCredentialKind.JumpPassword);
    }

    [Fact]
    public async Task JumpSecretWithoutARoute_IsRejectedBeforeAnythingIsStaged()
    {
        var fixture = new ProfileFixture();
        using var target = Secret("target");
        using var jump = Secret("jump");

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Profiles.AddAsync(new ServerProfileInput
        {
            Configuration = PasswordInput(),
            CredentialChange = CredentialChange.Replace(target),
            JumpCredentialChange = CredentialChange.Replace(jump)
        }));

        Assert.Empty(fixture.Credentials.Values);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static ServerRoute BrokenRoute(ServerValidationErrorCode code) => code switch
    {
        ServerValidationErrorCode.JumpHostRequired => new() { Jump = Jump() with { Host = " " } },
        ServerValidationErrorCode.JumpPortOutOfRange => new() { Jump = Jump() with { Port = 70000 } },
        ServerValidationErrorCode.JumpUsernameRequired => new() { Jump = Jump() with { Username = "" } },
        ServerValidationErrorCode.JumpAuthenticationMethodRequired => new() { Jump = Jump() with { AuthenticationMethod = AuthenticationMethod.NotConfigured } },
        ServerValidationErrorCode.JumpPrivateKeyPathRequired => new() { Jump = Jump() with { PrivateKeyPath = null } },
        ServerValidationErrorCode.JumpCredentialReferenceRequired => new() { Jump = Jump() with { AuthenticationMethod = AuthenticationMethod.Password, CredentialReferenceId = null } },
        ServerValidationErrorCode.JumpCredentialReferenceInvalid => new() { Jump = Jump() with { CredentialReferenceId = Guid.Empty } },
        ServerValidationErrorCode.RouteJumpRequired => new() { Jump = null },
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };

    private static JumpHop Jump() => new()
    {
        Host = "bastion.example.test",
        Port = 22,
        Username = "jump",
        AuthenticationMethod = AuthenticationMethod.SshKey,
        PrivateKeyPath = KeyPath
    };

    private static ServerRoute PasswordRoute() => new()
    {
        Jump = Jump() with { AuthenticationMethod = AuthenticationMethod.Password, PrivateKeyPath = null }
    };

    private static ServerInput DirectInput() => new()
    {
        Name = "Server",
        Host = "10.0.0.5",
        Port = 22,
        Username = "monitor",
        AuthenticationMethod = AuthenticationMethod.SshKey,
        PrivateKeyPath = KeyPath
    };

    private static ServerInput PasswordInput() => DirectInput() with
    {
        AuthenticationMethod = AuthenticationMethod.Password,
        PrivateKeyPath = null
    };

    private static Server PersistedServer() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Persisted",
        Host = "10.0.0.6",
        Port = 22,
        Username = "monitor",
        AuthenticationMethod = AuthenticationMethod.SshKey,
        PrivateKeyPath = KeyPath,
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    private static SecretValue Secret(string value) => new(value.AsSpan());

    private sealed class ProfileFixture
    {
        public ProfileFixture(bool throwOnSave = false)
        {
            Repository = new InMemoryRepository([]) { ThrowOnSave = throwOnSave };
            Service = new ServerService(Repository, new ServerValidator());
            Profiles = new ServerProfileService(Service, Credentials);
        }

        public InMemoryRepository Repository { get; }

        public ServerService Service { get; }

        public RecordingCredentialStore Credentials { get; } = new();

        public ServerProfileService Profiles { get; }

        public async Task<Server> CreateRoutedAsync()
        {
            using var target = Secret("target");
            using var jump = Secret("jump");
            var result = await Profiles.AddAsync(new ServerProfileInput
            {
                Configuration = PasswordInput() with { Route = PasswordRoute() },
                CredentialChange = CredentialChange.Replace(target),
                JumpCredentialChange = CredentialChange.Replace(jump)
            });
            Assert.True(result.Succeeded);
            Assert.Equal(2, Credentials.Values.Count);
            return result.Server!;
        }
    }

    private sealed class InMemoryRepository(IEnumerable<Server> initial) : IServerRepository
    {
        public List<Server> Saved { get; private set; } = initial.ToList();

        public bool ThrowOnSave { get; set; }

        public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Server>>(Saved.ToArray());

        public Task SaveAllAsync(IReadOnlyCollection<Server> servers, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSave)
            {
                throw new IOException("Synthetic persistence failure.");
            }

            Saved = servers.ToList();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCredentialStore : IServerCredentialStore
    {
        public Dictionary<CredentialReference, string> Values { get; } = [];

        public ServerCredentialKind? FailWritesOf { get; set; }

        public ServerCredentialKind? FailDeletesOf { get; set; }

        public Task WriteAsync(CredentialReference reference, SecretValue secret, CancellationToken cancellationToken = default)
        {
            if (reference.Kind == FailWritesOf)
            {
                throw new IOException("Synthetic credential write failure.");
            }

            Values[reference] = new string(secret.Reveal());
            return Task.CompletedTask;
        }

        public Task<SecretValue?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryGetValue(reference, out var value) ? new SecretValue(value.AsSpan()) : null);

        public Task<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default)
        {
            if (reference.Kind == FailDeletesOf)
            {
                throw new IOException("Synthetic credential delete failure.");
            }

            return Task.FromResult(Values.Remove(reference));
        }
    }
}
