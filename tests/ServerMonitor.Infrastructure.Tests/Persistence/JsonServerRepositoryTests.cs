using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.Infrastructure.Tests.Persistence;

public sealed class JsonServerRepositoryTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        "ServerMonitor.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndReadAsync_PersistsOnlyServerConfiguration()
    {
        var filePath = Path.Combine(_testDirectory, "servers.json");
        using var repository = CreateRepository(filePath);
        var server = CreateServer();

        await repository.SaveAllAsync([server]);
        var restored = Assert.Single(await repository.GetAllAsync());
        var json = await File.ReadAllTextAsync(filePath);

        Assert.Equal(server, restored);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NewRepositoryInstance_ReadsDataAfterSimulatedRestart()
    {
        var filePath = Path.Combine(_testDirectory, "servers.json");
        var server = CreateServer();

        using (var firstInstance = CreateRepository(filePath))
        {
            await firstInstance.SaveAllAsync([server]);
        }

        using var restartedInstance = CreateRepository(filePath);
        var restored = Assert.Single(await restartedInstance.GetAllAsync());

        Assert.Equal(server, restored);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsEmptyForInvalidJson()
    {
        var filePath = Path.Combine(_testDirectory, "servers.json");
        Directory.CreateDirectory(_testDirectory);
        await File.WriteAllTextAsync(filePath, "{ invalid json");
        using var repository = CreateRepository(filePath);

        var result = await repository.GetAllAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsEmptyWhenFileDoesNotExist()
    {
        using var repository = CreateRepository(Path.Combine(_testDirectory, "missing.json"));

        Assert.Empty(await repository.GetAllAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private static JsonServerRepository CreateRepository(string filePath) =>
        new(
            new ServerStorageOptions { FilePath = filePath },
            NullLogger<JsonServerRepository>.Instance);

    private static Server CreateServer() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Servidor de teste",
        Host = "server.example.test",
        Port = 22,
        Username = "monitor",
        OperatingSystem = ServerOperatingSystem.Linux,
        IsHidden = false,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
