using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;
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
        Assert.DoesNotContain("passphrase", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BEGIN OPENSSH PRIVATE KEY", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("privateKeyPath", json, StringComparison.Ordinal);
        Assert.Contains("credentialReferenceId", json, StringComparison.Ordinal);
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

    [Fact]
    public async Task GetAllAsync_MigratesMilestone2ConfigurationAsNotConfigured()
    {
        var filePath = Path.Combine(_testDirectory, "servers.json");
        Directory.CreateDirectory(_testDirectory);
        await File.WriteAllTextAsync(filePath, """
            [
              {
                "id": "de305d54-75b4-431b-adb2-eb6b9e546014",
                "name": "Legacy server",
                "host": "legacy.example.test",
                "port": 22,
                "username": "monitor",
                "operatingSystem": 1,
                "isHidden": false,
                "createdAt": "2026-01-01T00:00:00+00:00"
              }
            ]
            """);
        using var repository = CreateRepository(filePath);

        var server = Assert.Single(await repository.GetAllAsync());

        Assert.Equal(AuthenticationMethod.NotConfigured, server.AuthenticationMethod);
        Assert.Null(server.PrivateKeyPath);
        Assert.Null(server.CredentialReferenceId);
    }

    [Fact]
    public async Task GetAllAsync_PreM6ConfigurationWithoutRefreshInterval_DefaultsToThirtySeconds()
    {
        // A servers.json written before M6 has no refreshIntervalSeconds; it must migrate to the
        // 30 s default while every other field — credential reference, key path, host, trust-
        // relevant identity — is preserved untouched.
        var filePath = Path.Combine(_testDirectory, "servers.json");
        Directory.CreateDirectory(_testDirectory);
        var credentialId = "7b1f2c3d-4e5f-6a7b-8c9d-0e1f2a3b4c5d";
        await File.WriteAllTextAsync(filePath, $$"""
            [
              {
                "id": "de305d54-75b4-431b-adb2-eb6b9e546014",
                "name": "Legacy server",
                "host": "legacy.example.test",
                "port": 2222,
                "username": "monitor",
                "operatingSystem": 1,
                "authenticationMethod": 1,
                "privateKeyPath": "C:\\keys\\id_ed25519",
                "credentialReferenceId": "{{credentialId}}",
                "isHidden": true,
                "createdAt": "2026-01-01T00:00:00+00:00"
              }
            ]
            """);
        using var repository = CreateRepository(filePath);

        var server = Assert.Single(await repository.GetAllAsync());

        Assert.Equal(RefreshIntervalPolicy.DefaultSeconds, server.RefreshIntervalSeconds);
        Assert.Equal(30, server.RefreshIntervalSeconds);
        // Nothing else changed by the migration.
        Assert.Equal(2222, server.Port);
        Assert.Equal("legacy.example.test", server.Host);
        Assert.Equal("monitor", server.Username);
        Assert.Equal(AuthenticationMethod.SshKey, server.AuthenticationMethod);
        Assert.Equal("C:\\keys\\id_ed25519", server.PrivateKeyPath);
        Assert.Equal(Guid.Parse(credentialId), server.CredentialReferenceId);
        Assert.True(server.IsHidden);
    }

    [Fact]
    public async Task SaveAndReadAsync_RoundTripsRefreshInterval()
    {
        var filePath = Path.Combine(_testDirectory, "servers.json");
        using var repository = CreateRepository(filePath);
        var server = CreateServer() with { RefreshIntervalSeconds = 300 };

        await repository.SaveAllAsync([server]);
        var restored = Assert.Single(await repository.GetAllAsync());

        Assert.Equal(300, restored.RefreshIntervalSeconds);
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
        AuthenticationMethod = AuthenticationMethod.SshKey,
        PrivateKeyPath = Path.Combine(Path.GetTempPath(), "id_test"),
        CredentialReferenceId = Guid.NewGuid(),
        IsHidden = false,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
