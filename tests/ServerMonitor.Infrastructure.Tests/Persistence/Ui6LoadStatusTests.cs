using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.Infrastructure.Tests.Persistence;

public sealed class Ui6LoadStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ui6-load-" + Guid.NewGuid());
    private string Direct => Path.Combine(_root, "servers.json");
    private string Routed => Path.Combine(_root, "routed-servers.json");
    private JsonServerRepository Repository() => new(new ServerStorageOptions { FilePath = Direct },
        NullLogger<JsonServerRepository>.Instance, new ConfigurationWriteGate());
    private async Task Write(string path, string text) { Directory.CreateDirectory(_root); await File.WriteAllTextAsync(path, text); }

    [Fact]
    public async Task AbsentBoth_IsNotFound_AndLegacyListIsEmpty()
    {
        using var repository = Repository();
        Assert.Equal(ServerLoadStatus.NotFound, await repository.GetLoadStatusAsync());
        Assert.Empty(await repository.GetAllAsync());
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("[]", ServerLoadStatus.Loaded)]
    [InlineData("{ invalid", ServerLoadStatus.Unavailable)]
    [InlineData("{}", ServerLoadStatus.Unavailable)]
    [InlineData("[null]", ServerLoadStatus.Unavailable)]
    public async Task DirectStates_PreserveBytesAndLegacyList(string json, ServerLoadStatus expected)
    {
        await Write(Direct, json);
        var before = await File.ReadAllBytesAsync(Direct);
        using var repository = Repository();
        Assert.Empty(await repository.GetAllAsync());
        Assert.Equal(expected, await repository.GetLoadStatusAsync());
        Assert.Empty(await repository.GetAllAsync());
        Assert.Equal(before, await File.ReadAllBytesAsync(Direct));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData(1, ServerLoadStatus.Loaded)]
    [InlineData(2, ServerLoadStatus.Unavailable)]
    public async Task RoutedOnly_EmptyOrFutureSchema(int schema, ServerLoadStatus expected)
    {
        await Write(Routed, "{\"schemaVersion\":" + schema + ",\"servers\":[]}");
        using var repository = Repository();
        Assert.Equal(expected, await repository.GetLoadStatusAsync());
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task LockedFile_IsUnavailable_AndLegacyStillReturnsEmpty()
    {
        await Write(Direct, "[]");
        using var locked = new FileStream(Direct, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var repository = Repository();
        Assert.Equal(ServerLoadStatus.Unavailable, await repository.GetLoadStatusAsync());
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task MixedQuarantineAndValid_IsLoaded_WithoutChangingLegacyResults()
    {
        var server = new Server { Id = Guid.NewGuid(), Name = "test", Host = "test.local", Username = "qa" };
        var json = JsonSerializer.Serialize(server, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await Write(Direct, "[null," + json + "]");
        using var repository = Repository();
        Assert.Equal(ServerLoadStatus.Loaded, await repository.GetLoadStatusAsync());
        Assert.Equal(server.Id, Assert.Single(await repository.GetAllAsync()).Id);
    }

    [Fact]
    public async Task ServiceStatusAndList_RemainProcessCached()
    {
        using var repository = Repository();
        using var service = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());
        Assert.Equal(ServerLoadStatus.NotFound, await service.GetLoadStatusAsync());
        await Write(Direct, "invalid");
        Assert.Equal(ServerLoadStatus.NotFound, await service.GetLoadStatusAsync());
        Assert.Empty(await service.GetAllAsync());
    }

    [Fact]
    public async Task DirectoryAtConfigPath_IsUnavailable()
    {
        Directory.CreateDirectory(Direct);
        using var repository = Repository();
        Assert.Equal(ServerLoadStatus.Unavailable, await repository.GetLoadStatusAsync());
    }

    [Fact]
    public async Task DomainQuarantineWithNoValidServers_IsUnavailable_AndLegacyFilteringIsUnchanged()
    {
        var server = new Server { Id = Guid.NewGuid(), Name = "invalid", Host = "", Username = "" };
        await Write(Direct, JsonSerializer.Serialize(new[] { server }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var repository = Repository();
        Assert.Single(await repository.GetAllAsync());
        using var service = new ServerService(repository, new ServerValidator(), new ConfigurationWriteGate());
        Assert.Empty(await service.GetAllAsync());
        Assert.Equal(ServerLoadStatus.Unavailable, await service.GetLoadStatusAsync());
        Assert.Empty(await service.GetAllAsync());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
