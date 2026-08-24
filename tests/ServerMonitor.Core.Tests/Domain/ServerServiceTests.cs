using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Tests.Domain;

public sealed class ServerServiceTests
{
    [Fact]
    public async Task AddAsync_CreatesServerWithIdentityAndTimestamp()
    {
        using var service = CreateService();

        var result = await service.AddAsync(CreateInput());

        Assert.True(result.Succeeded);
        Assert.NotEqual(Guid.Empty, result.Server!.Id);
        Assert.Equal(22, result.Server.Port);
        Assert.False(result.Server.IsHidden);
        Assert.True(result.Server.CreatedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task AddAsync_TrimsUserInput()
    {
        using var service = CreateService();

        var result = await service.AddAsync(CreateInput() with
        {
            Name = "  Servidor  ",
            Host = "  host.example.test ",
            Username = " monitor "
        });

        Assert.Equal("Servidor", result.Server!.Name);
        Assert.Equal("host.example.test", result.Server.Host);
        Assert.Equal("monitor", result.Server.Username);
    }

    [Fact]
    public async Task AddAsync_DoesNotPersistInvalidInput()
    {
        var repository = new InMemoryServerRepository();
        using var service = CreateService(repository);

        var result = await service.AddAsync(CreateInput() with { Port = -1 });

        Assert.False(result.Succeeded);
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task AddAsync_RejectsNullFieldsWithoutThrowing()
    {
        var repository = new InMemoryServerRepository();
        using var service = CreateService(repository);
        var input = CreateInput() with
        {
            Name = null!,
            Host = null!,
            Username = null!
        };

        var result = await service.AddAsync(input);

        Assert.False(result.Succeeded);
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task UpdateAsync_ChangesEditableFieldsAndPreservesIdentity()
    {
        using var service = CreateService();
        var created = (await service.AddAsync(CreateInput())).Server!;

        var result = await service.UpdateAsync(created.Id, CreateInput() with
        {
            Name = "Servidor atualizado",
            OperatingSystem = ServerOperatingSystem.MacOS
        });

        Assert.True(result.Succeeded);
        Assert.Equal(created.Id, result.Server!.Id);
        Assert.Equal(created.CreatedAt, result.Server.CreatedAt);
        Assert.Equal("Servidor atualizado", result.Server.Name);
        Assert.Equal(ServerOperatingSystem.MacOS, result.Server.OperatingSystem);
    }

    [Fact]
    public async Task HideAndRestoreAsync_TogglesVisibility()
    {
        using var service = CreateService();
        var server = (await service.AddAsync(CreateInput())).Server!;

        Assert.True(await service.HideAsync(server.Id));
        Assert.True((await service.GetAllAsync()).Single().IsHidden);

        Assert.True(await service.RestoreAsync(server.Id));
        Assert.False((await service.GetAllAsync()).Single().IsHidden);
    }

    [Fact]
    public async Task RemoveAsync_DeletesServer()
    {
        using var service = CreateService();
        var server = (await service.AddAsync(CreateInput())).Server!;

        Assert.True(await service.RemoveAsync(server.Id));
        Assert.Empty(await service.GetAllAsync());
    }

    [Fact]
    public async Task MissingServerOperations_ReturnFalseWithoutCrashing()
    {
        using var service = CreateService();
        var missingId = Guid.NewGuid();

        Assert.False(await service.HideAsync(missingId));
        Assert.False(await service.RestoreAsync(missingId));
        Assert.False(await service.RemoveAsync(missingId));
        Assert.False((await service.UpdateAsync(missingId, CreateInput())).Succeeded);
    }

    private static ServerService CreateService(InMemoryServerRepository? repository = null) =>
        new(repository ?? new InMemoryServerRepository(), new ServerValidator());

    private static ServerInput CreateInput() => new()
    {
        Name = "Servidor de teste",
        Host = "host.example.test",
        Port = 22,
        Username = "monitor",
        OperatingSystem = ServerOperatingSystem.Auto,
        AuthenticationMethod = AuthenticationMethod.SshKey,
        PrivateKeyPath = Path.Combine(Path.GetTempPath(), "id_test")
    };

    private sealed class InMemoryServerRepository : IServerRepository
    {
        private List<Server> _servers = [];

        public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Server>>(_servers.ToArray());

        public Task SaveAllAsync(
            IReadOnlyCollection<Server> servers,
            CancellationToken cancellationToken = default)
        {
            _servers = servers.ToList();
            return Task.CompletedTask;
        }
    }
}
