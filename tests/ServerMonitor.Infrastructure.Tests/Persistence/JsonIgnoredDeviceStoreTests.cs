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

    /// <summary>
    /// UI.12 C-E5 (Cortex UI.12A, data loss). A transient I/O failure on the first read cached an EMPTY set; the next
    /// IgnoreAsync then wrote "empty ∪ {new}" over the file and every previously ignored device was silently lost. The
    /// failure here is a real sharing violation (the file held open with FileShare.None by the test), on a temp path.
    /// </summary>
    [Fact]
    public async Task ATransientReadFailure_ThenIgnore_KeepsEveryPreviouslyIgnoredDevice()
    {
        var path = Path.Combine(_testDirectory, "ignored-devices.json");
        var a = Hash("device-a");
        var b = Hash("device-b");
        var c = Hash("device-c");
        using (var seed = Create(path))
        {
            Assert.True(await seed.IgnoreAsync(a));
            Assert.True(await seed.IgnoreAsync(b));
        }

        using var store = Create(path);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(await store.LoadAsync()); // unreadable right now: nothing shown as ignored, nothing cached
        }

        Assert.True(await store.IgnoreAsync(c));    // the file is readable again: re-read, then add

        using var restarted = Create(path);
        Assert.Equal(new HashSet<string> { a, b, c }, await restarted.LoadAsync());
    }

    [Fact]
    public async Task IgnoreWhileTheFileIsUnreadable_IsRefused_AndTheFileIsUntouched()
    {
        var path = Path.Combine(_testDirectory, "ignored-devices.json");
        var a = Hash("device-a");
        using (var seed = Create(path))
        {
            Assert.True(await seed.IgnoreAsync(a));
        }

        var before = await File.ReadAllBytesAsync(path);
        using var store = Create(path);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(await store.IgnoreAsync(Hash("device-b")));
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal([a], await store.LoadAsync());
    }

    /// <summary>
    /// UI.12 Relay r2 R2-01: "Repor" is an explicit request to clear the list, so it must not depend on reading it (an
    /// unreadable file must not block the user's reset). Pinned STRUCTURALLY: no lock was found that makes the read fail
    /// while the rename-based replace still succeeds - measured here, File.Move(overwrite) is denied against any open handle
    /// on the destination, FileShare.Delete included. So ResetAsync must not call EnsureLoadedAsync at all.
    /// </summary>
    [Fact]
    public void Reset_DoesNotReadTheFileFirst()
    {
        var source = File.ReadAllText(StoreSource());
        var start = source.IndexOf("public async Task ResetAsync(", StringComparison.Ordinal);
        var end = source.IndexOf("public void Dispose()", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var reset = source[start..end];

        Assert.DoesNotContain("EnsureLoadedAsync", reset, StringComparison.Ordinal);
        Assert.Contains("await SaveAsync(candidate, cancellationToken)", reset, StringComparison.Ordinal);
    }

    private static string StoreSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "ServerMonitor.Infrastructure", "Persistence", "JsonIgnoredDeviceStore.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("JsonIgnoredDeviceStore.cs not found above the test output.");
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
