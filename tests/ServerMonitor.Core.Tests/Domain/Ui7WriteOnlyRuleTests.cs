using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Tests.Domain;

/// <summary>
/// UI.7 H-UI7-1: the write-only format rules (host with scheme/path/whitespace, a .pub key for the target or the jump host,
/// a jump host that is the target itself) are enforced by <see cref="ServerValidator.ValidateDraft"/> and by
/// <see cref="ServerValidator.Validate(ServerInput)"/> (the Add/Update path of <see cref="ServerService"/>), and NEVER by
/// <see cref="ServerValidator.Validate(Server)"/> (load/quarantine and backup restore).
/// </summary>
public sealed class Ui7WriteOnlyRuleTests
{
    private readonly ServerValidator _validator = new();

    public static TheoryData<string, ServerInput, ServerValidationErrorCode> Violations() => new()
    {
        { "scheme", Input() with { Host = "https://server.example.com" }, ServerValidationErrorCode.HostFormatInvalid },
        { "path", Input() with { Host = "server.example.com/admin" }, ServerValidationErrorCode.HostFormatInvalid },
        { "backslash", Input() with { Host = @"server\admin" }, ServerValidationErrorCode.HostFormatInvalid },
        { "inner space", Input() with { Host = "server example.com" }, ServerValidationErrorCode.HostFormatInvalid },
        { "tab", Input() with { Host = "server\texample.com" }, ServerValidationErrorCode.HostFormatInvalid },
        { "target .pub", Input() with { PrivateKeyPath = @"C:\keys\id_ed25519.pub" }, ServerValidationErrorCode.PrivateKeyIsPublicKey },
        { "target .PUB", Input() with { PrivateKeyPath = @"C:\keys\id_ed25519.PUB" }, ServerValidationErrorCode.PrivateKeyIsPublicKey },
        { "jump .pub", Routed("bastion.example.com", jumpKey: @"C:\keys\bastion.pub"), ServerValidationErrorCode.JumpPrivateKeyIsPublicKey },
        { "jump == target", Routed("10.0.0.5"), ServerValidationErrorCode.JumpEndpointIsTarget },
        { "jump == target (normalized)", Routed("10.0.0.5.") with { Host = " 10.0.0.5 " }, ServerValidationErrorCode.JumpEndpointIsTarget },
        { "jump == target (case)", Routed("Bastion.Example.COM") with { Host = "bastion.example.com" }, ServerValidationErrorCode.JumpEndpointIsTarget },
    };

    [Theory]
    [MemberData(nameof(Violations))]
    public void ADraftThatBreaksAWriteRule_IsRejected(string name, ServerInput input, ServerValidationErrorCode code)
    {
        _ = name;
        Assert.Contains(_validator.ValidateDraft(input).Errors, error => error.Code == code);
    }

    [Theory]
    [MemberData(nameof(Violations))]
    public void ANewWrite_ThatBreaksAWriteRule_IsRejected(string name, ServerInput input, ServerValidationErrorCode code)
    {
        _ = name;
        Assert.Contains(_validator.Validate(input).Errors, error => error.Code == code);
    }

    [Theory]
    [MemberData(nameof(Violations))]
    public void AStoredServer_ThatBreaksAWriteRule_StillValidates(string name, ServerInput input, ServerValidationErrorCode code)
    {
        _ = name;
        _ = code;
        var stored = Stored(input);
        Assert.True(_validator.Validate(stored).IsValid);
    }

    public static TheoryData<string, ServerInput> Accepted() => new()
    {
        { "plain host", Input() },
        { "ipv4", Input() with { Host = "192.168.1.10" } },
        { "ipv6", Input() with { Host = "fe80::1" } },
        { "surrounding spaces (trimmed by the normalizer)", Input() with { Host = "  server.example.com  " } },
        { "private key", Input() with { PrivateKeyPath = @"C:\keys\id_ed25519" } },
        { "password auth ignores the key path", Input() with { AuthenticationMethod = AuthenticationMethod.Password, PrivateKeyPath = @"C:\keys\id.pub" } },
        { "jump on another port of the same host", Routed("10.0.0.5") with { Port = 2222 } },
        { "jump elsewhere", Routed("bastion.example.com") },
        { "jump password ignores the key path", Routed("bastion.example.com", jumpKey: @"C:\keys\bastion.pub", jumpPassword: true) },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void AValidWrite_IsUnchanged(string name, ServerInput input)
    {
        _ = name;
        Assert.DoesNotContain(_validator.ValidateDraft(input).Errors, error => IsWriteRule(error.Code));
        Assert.DoesNotContain(_validator.Validate(input).Errors, error => IsWriteRule(error.Code));
    }

    [Fact]
    public async Task ServerService_Add_RejectsAWriteRuleViolation_AndPersistsNothing()
    {
        var repository = new Repository();
        using var service = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());

        var result = await service.AddAsync(Input() with { Host = "https://server.example.com" });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Validation.Errors, error => error.Code == ServerValidationErrorCode.HostFormatInvalid);
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task ServerService_Update_RejectsAWriteRuleViolation_AndKeepsTheStoredServer()
    {
        var repository = new Repository();
        using var service = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());
        var added = await service.AddAsync(Input());
        Assert.True(added.Succeeded);

        var result = await service.UpdateAsync(added.Server!.Id, Routed("10.0.0.5"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Validation.Errors, error => error.Code == ServerValidationErrorCode.JumpEndpointIsTarget);
        var stored = Assert.Single(await repository.GetAllAsync());
        Assert.Null(stored.Route);
    }

    [Fact]
    public async Task ServerService_Load_KeepsAStoredServerThatBreaksTheWriteRules_NotQuarantined()
    {
        var repository = new Repository();
        var stored = new[]
        {
            Stored(Input() with { Host = "https://legacy.example.com" }),
            Stored(Input() with { PrivateKeyPath = @"C:\keys\id_ed25519.pub" }),
            Stored(Routed("host.example.test")),
            Stored(Routed("bastion.example.com", jumpKey: @"C:\keys\bastion.pub")),
        };
        await repository.SaveAllAsync(stored);
        using var service = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());

        var loaded = await service.GetAllAsync();

        Assert.Equal(stored.Select(server => server.Id).Order(), loaded.Select(server => server.Id).Order());
        Assert.Equal(ServerLoadStatus.Loaded, await service.GetLoadStatusAsync());
    }

    [Fact]
    public async Task ServerService_ALegacyServer_CanStillBeHiddenAndRestored()
    {
        var repository = new Repository();
        var legacy = Stored(Input() with { Host = "https://legacy.example.com" });
        await repository.SaveAllAsync([legacy]);
        using var service = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());

        Assert.True(await service.HideAsync(legacy.Id));
        Assert.True(await service.RestoreAsync(legacy.Id));
        Assert.Single(await service.GetAllAsync());
    }

    private static bool IsWriteRule(ServerValidationErrorCode code) => code is
        ServerValidationErrorCode.HostFormatInvalid
        or ServerValidationErrorCode.PrivateKeyIsPublicKey
        or ServerValidationErrorCode.JumpPrivateKeyIsPublicKey
        or ServerValidationErrorCode.JumpEndpointIsTarget;

    private static ServerInput Input() => new()
    {
        Name = "Servidor de teste",
        Host = "host.example.test",
        Port = 22,
        Username = "monitor",
        OperatingSystem = ServerOperatingSystem.Linux,
        AuthenticationMethod = AuthenticationMethod.SshKey,
        PrivateKeyPath = @"C:\keys\id_ed25519"
    };

    private static ServerInput Routed(string jumpHost, string? jumpKey = null, bool jumpPassword = false) => Input() with
    {
        Host = "10.0.0.5",
        Route = new ServerRoute
        {
            Jump = new JumpHop
            {
                Host = jumpHost,
                Port = 22,
                Username = "jump",
                AuthenticationMethod = jumpPassword ? AuthenticationMethod.Password : AuthenticationMethod.SshKey,
                PrivateKeyPath = jumpKey ?? @"C:\keys\bastion",
                CredentialReferenceId = jumpPassword ? Guid.Parse("00000000-0000-0000-0000-000000000077") : null
            }
        }
    };

    private static Server Stored(ServerInput input) => new()
    {
        Id = Guid.NewGuid(),
        Name = input.Name,
        Host = input.Host.Trim(),
        Port = input.Port,
        Username = input.Username,
        OperatingSystem = input.OperatingSystem,
        AuthenticationMethod = input.AuthenticationMethod,
        PrivateKeyPath = input.PrivateKeyPath,
        CredentialReferenceId = input.CredentialReferenceId,
        RefreshIntervalSeconds = 30,
        Route = input.Route,
        CreatedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
    };

    private sealed class Repository : IServerRepository
    {
        private List<Server> _servers = [];

        public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Server>>(_servers.ToArray());

        public Task SaveAllAsync(IReadOnlyCollection<Server> servers, CancellationToken cancellationToken = default)
        {
            _servers = servers.ToList();
            return Task.CompletedTask;
        }
    }
}
