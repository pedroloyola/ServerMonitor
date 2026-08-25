using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.Infrastructure.Tests.Persistence;

public sealed class JsonIgnoredDeviceStoreTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(), "ServerMonitor.Discovery.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Ignore_PersistsAcrossSimulatedRestart()
    {
        var path = Path.Combine(_testDirectory, "ignored-devices.json");
        var a = Hash("device-a");

        using (var first = Create(path))
        {
            Assert.True(await first.IgnoreAsync(a));
        }

        using var restarted = Create(path);
        Assert.Equal([a], await restarted.LoadAsync());
    }

    [Fact]
    public async Task Ignore_WriteFailure_DoesNotCommitMemoryAndRetryPersistsAcrossRestart()
    {
        var path = Path.Combine(_testDirectory, "ignored-devices.json");
        var temporaryPath = path + ".tmp";
        var identity = Hash("retry-after-storage-recovery");
        Directory.CreateDirectory(temporaryPath);
        using var store = Create(path);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.IgnoreAsync(identity));
        Assert.DoesNotContain(identity, await store.LoadAsync());

        Directory.Delete(temporaryPath);
        Assert.True(await store.IgnoreAsync(identity));

        using var restarted = Create(path);
        Assert.Contains(identity, await restarted.LoadAsync());
    }

    [Fact]
    public async Task IgnoreA_DoesNotIgnoreB()
    {
        var path = Path.Combine(_testDirectory, "ignored-devices.json");
        var a = Hash("device-a");
        var b = Hash("device-b");
        using var store = Create(path);

        Assert.True(await store.IgnoreAsync(a));

        var entries = await store.LoadAsync();
        Assert.Contains(a, entries);
        Assert.DoesNotContain(b, entries);
    }

    [Fact]
    public async Task InvalidHash_IsRefusedAndNeverWritten()
    {
        var path = Path.Combine(_testDirectory, "ignored-devices.json");
        using var store = Create(path);

        Assert.False(await store.IgnoreAsync("NOT-A-HASH"));

        Assert.Empty(await store.LoadAsync());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Capacity_RefusesNewIdentityButAcceptsExistingIdentity()
    {
        var path = Path.Combine(_testDirectory, "ignored-devices.json");
        Directory.CreateDirectory(_testDirectory);
        var existing = Enumerable.Range(0, DiscoveryInputPolicy.MaxIgnoredIdentities)
            .Select(index => Hash($"existing-{index}"))
            .ToArray();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(existing));
        using var store = Create(path);

        Assert.Equal(DiscoveryInputPolicy.MaxIgnoredIdentities, (await store.LoadAsync()).Count);
        Assert.True(await store.IgnoreAsync(existing[0]));
        Assert.False(await store.IgnoreAsync(Hash("overflow")));
        Assert.Equal(DiscoveryInputPolicy.MaxIgnoredIdentities, (await store.LoadAsync()).Count);
    }

    [Fact]
    public async Task Load_DeduplicatesAndDropsInvalidEntries()
    {
        var path = Path.Combine(_testDirectory, "ignored-devices.json");
        Directory.CreateDirectory(_testDirectory);
        var valid = Hash("valid");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[]
        {
            valid,
            valid,
            "invalid",
            valid.ToUpperInvariant()
        }));
        using var store = Create(path);

        Assert.Equal([valid], await store.LoadAsync());
    }

    [Fact]
    public async Task MalformedFile_LoadsEmptyAndResetRepairsIt()
    {
        var path = Path.Combine(_testDirectory, "ignored-devices.json");
        Directory.CreateDirectory(_testDirectory);
        await File.WriteAllTextAsync(path, "{ malformed json");
        using var store = Create(path);

        Assert.Empty(await store.LoadAsync());
        await store.ResetAsync();

        Assert.Equal("[]", NormalizeJson(await File.ReadAllTextAsync(path)));
        using var restarted = Create(path);
        Assert.Empty(await restarted.LoadAsync());
    }

    [Fact]
    public async Task OversizeFile_LoadsEmptyAndResetRepairsIt()
    {
        var path = Path.Combine(_testDirectory, "ignored-devices.json");
        Directory.CreateDirectory(_testDirectory);
        await File.WriteAllTextAsync(path,
            new string('x', DiscoveryInputPolicy.MaxIgnoreFileBytes + 1));
        using var store = Create(path);

        Assert.Empty(await store.LoadAsync());
        await store.ResetAsync();

        Assert.True(new FileInfo(path).Length < DiscoveryInputPolicy.MaxIgnoreFileBytes);
        Assert.Equal("[]", NormalizeJson(await File.ReadAllTextAsync(path)));
    }

    [Fact]
    public async Task PersistedDocumentContainsOnlyStableHashes()
    {
        var path = Path.Combine(_testDirectory, "ignored-devices.json");
        var hash = Hash("Mac Studio|_ssh._tcp|local");
        using var store = Create(path);

        Assert.True(await store.IgnoreAsync(hash));
        var json = await File.ReadAllTextAsync(path);

        Assert.Contains(hash, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Mac Studio", json, StringComparison.Ordinal);
        Assert.DoesNotContain("host", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fingerprint", json, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private static JsonIgnoredDeviceStore Create(string path) => new(
        new IgnoredDeviceStorageOptions { FilePath = path },
        NullLogger<JsonIgnoredDeviceStore>.Instance);

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string NormalizeJson(string json) =>
        JsonSerializer.Serialize(JsonSerializer.Deserialize<List<string>>(json));
}
