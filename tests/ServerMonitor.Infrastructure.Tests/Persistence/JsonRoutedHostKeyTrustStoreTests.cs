using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.Infrastructure.Tests.Persistence;

public sealed class JsonRoutedHostKeyTrustStoreTests : IDisposable
{
    private const string FingerprintA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string FingerprintB = "AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ServerMonitor.RoutedTrust.Tests",
        Guid.NewGuid().ToString("N"));

    private static readonly SshEndpoint BastionA = SshEndpoint.Create("bastion-a.example.test", 22);
    private static readonly SshEndpoint BastionB = SshEndpoint.Create("bastion-b.example.test", 22);
    private static readonly SshEndpoint PrivateTarget = SshEndpoint.Create("10.0.0.5", 22);

    private string DirectPath => Path.Combine(_directory, "known-hosts.json");

    private string RoutesPath => RoutedHostKeyTrustStorageOptions
        .From(new HostKeyTrustStorageOptions { FilePath = DirectPath })
        .FilePath;

    [Fact]
    public void RoutesFile_IsASiblingOfTheDirectFile_AndNeverTheSameFile()
    {
        Assert.Equal(Path.Combine(_directory, "known-hosts.routes.json"), RoutesPath);
        Assert.NotEqual(Path.GetFullPath(DirectPath), Path.GetFullPath(RoutesPath));
    }

    [Fact]
    public async Task SamePrivateTargetBehindTwoBastions_DoesNotCollide()
    {
        // H1: keyed by bare host:port, this pair raised HostKeyTrustConflictException in the direct store.
        using var store = CreateRouted();
        await store.TrustAsync(new SshRoute(BastionA, PrivateTarget), Identity(FingerprintA));
        await store.TrustAsync(new SshRoute(BastionB, PrivateTarget), Identity(FingerprintB));

        using var restarted = CreateRouted();
        var viaA = await restarted.GetAsync(new SshRoute(BastionA, PrivateTarget));
        var viaB = await restarted.GetAsync(new SshRoute(BastionB, PrivateTarget));

        Assert.True(viaA!.Identity.Matches(Identity(FingerprintA)));
        Assert.True(viaB!.Identity.Matches(Identity(FingerprintB)));
        Assert.Null(await restarted.GetAsync(new SshRoute(SshEndpoint.Create("bastion-c.example.test", 22), PrivateTarget)));
    }

    [Fact]
    public async Task Route_IsNormalizedLikeTheDirectStore()
    {
        using var store = CreateRouted();
        await store.TrustAsync(
            new SshRoute(new SshEndpoint("Bastion-A.Example.TEST.", 22), PrivateTarget),
            Identity(FingerprintA));

        var restored = await store.GetAsync(SshRoute.Create(BastionA, PrivateTarget));

        Assert.NotNull(restored);
        Assert.Equal(BastionA, restored.Route.Via);
    }

    [Fact]
    public async Task Trust_KeepsConflictSemantics()
    {
        using var store = CreateRouted();
        var route = new SshRoute(BastionA, PrivateTarget);
        await store.TrustAsync(route, Identity(FingerprintA));
        await store.TrustAsync(route, Identity(FingerprintA)); // same key: no-op

        await Assert.ThrowsAsync<HostKeyTrustConflictException>(() => store.TrustAsync(route, Identity(FingerprintB)));
        Assert.True((await store.GetAsync(route))!.Identity.Matches(Identity(FingerprintA)));
    }

    [Fact]
    public async Task Remove_DeletesOnlyThatRoute()
    {
        using var store = CreateRouted();
        await store.TrustAsync(new SshRoute(BastionA, PrivateTarget), Identity(FingerprintA));
        await store.TrustAsync(new SshRoute(BastionB, PrivateTarget), Identity(FingerprintB));

        Assert.True(await store.RemoveAsync(new SshRoute(BastionA, PrivateTarget)));
        Assert.False(await store.RemoveAsync(new SshRoute(BastionA, PrivateTarget)));
        Assert.Null(await store.GetAsync(new SshRoute(BastionA, PrivateTarget)));
        Assert.NotNull(await store.GetAsync(new SshRoute(BastionB, PrivateTarget)));
    }

    [Fact]
    public async Task DirectLookup_NeverSeesARoutedEntry_AndItsFileIsUntouched()
    {
        using var direct = CreateDirect();
        await direct.TrustAsync(BastionA, Identity(FingerprintA));
        var directBytes = await File.ReadAllBytesAsync(DirectPath);

        using var routed = CreateRouted();
        await routed.TrustAsync(new SshRoute(BastionA, PrivateTarget), Identity(FingerprintB));

        using var restartedDirect = CreateDirect();
        Assert.Null(await restartedDirect.GetAsync(PrivateTarget));
        Assert.True((await restartedDirect.GetAsync(BastionA))!.Identity.Matches(Identity(FingerprintA)));
        Assert.Equal(directBytes, await File.ReadAllBytesAsync(DirectPath));
        Assert.True(File.Exists(RoutesPath));
    }

    [Fact]
    public async Task RoutedLookup_NeverSeesADirectEntry_AndDirectWritesLeaveTheRoutesFileAlone()
    {
        using var routed = CreateRouted();
        await routed.TrustAsync(new SshRoute(BastionB, PrivateTarget), Identity(FingerprintB));
        var routesBytes = await File.ReadAllBytesAsync(RoutesPath);

        using var direct = CreateDirect();
        await direct.TrustAsync(PrivateTarget, Identity(FingerprintA));
        await direct.TrustAsync(BastionA, Identity(FingerprintA));

        using var restartedRouted = CreateRouted();
        Assert.Null(await restartedRouted.GetAsync(new SshRoute(BastionA, PrivateTarget)));
        Assert.True((await restartedRouted.GetAsync(new SshRoute(BastionB, PrivateTarget)))!.Identity.Matches(Identity(FingerprintB)));
        Assert.Equal(routesBytes, await File.ReadAllBytesAsync(RoutesPath));
    }

    [Theory]
    [InlineData("{ not-json }")]
    [InlineData("[]")]
    [InlineData("""{ "entries": [] }""")]
    [InlineData("""{ "schemaVersion": 0, "entries": [] }""")]
    [InlineData("""{ "schemaVersion": 1 }""")]
    [InlineData("""{ "schemaVersion": 1, "entries": [ { "via": { "host": "bastion-a.example.test", "port": 22 }, "endpoint": { "host": "10.0.0.5", "port": 22 }, "identity": { "algorithm": "ssh-ed25519", "sha256Fingerprint": "invalid" } } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "entries": [ { "endpoint": { "host": "10.0.0.5", "port": 22 }, "identity": { "algorithm": "ssh-ed25519", "sha256Fingerprint": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" } } ] }""")]
    public async Task InvalidRoutesFile_FailsClosedOnEveryOperation_AndIsNeverOverwritten(string content)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(RoutesPath, content);
        var before = await File.ReadAllBytesAsync(RoutesPath);
        using var store = CreateRouted();
        var route = new SshRoute(BastionA, PrivateTarget);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(route));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(route));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.TrustAsync(route, Identity(FingerprintA)));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RemoveAsync(route));
        Assert.Equal(before, await File.ReadAllBytesAsync(RoutesPath));
    }

    [Fact]
    public async Task DuplicateRoute_FailsClosed()
    {
        Directory.CreateDirectory(_directory);
        const string entry = """{ "via": { "host": "bastion-a.example.test", "port": 22 }, "endpoint": { "host": "10.0.0.5", "port": 22 }, "identity": { "algorithm": "ssh-ed25519", "sha256Fingerprint": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" } }""";
        await File.WriteAllTextAsync(RoutesPath, $$"""{ "schemaVersion": 1, "entries": [ {{entry}}, {{entry}} ] }""");
        using var store = CreateRouted();

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(new SshRoute(BastionA, PrivateTarget)));
    }

    [Fact]
    public async Task FutureSchema_FailsClosedForRoutedLookups_AndIsNeverRewritten()
    {
        Directory.CreateDirectory(_directory);
        const string future = """
            { "schemaVersion": 2, "entries": [ { "via": { "host": "bastion-a.example.test", "port": 22 },
              "endpoint": { "host": "10.0.0.5", "port": 22 },
              "identity": { "algorithm": "ssh-ed25519", "sha256Fingerprint": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" },
              "confirmedAt": "2026-09-29T00:00:00+00:00" } ] }
            """;
        await File.WriteAllTextAsync(RoutesPath, future);
        var before = await File.ReadAllBytesAsync(RoutesPath);
        using var store = CreateRouted();
        var route = new SshRoute(BastionA, PrivateTarget);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(route));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.TrustAsync(new SshRoute(BastionB, PrivateTarget), Identity(FingerprintB)));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RemoveAsync(route));
        Assert.Equal(before, await File.ReadAllBytesAsync(RoutesPath));

        // The direct store is unaffected by the routed file's state.
        using var direct = CreateDirect();
        await direct.TrustAsync(BastionA, Identity(FingerprintA));
        Assert.NotNull(await direct.GetAsync(BastionA));
    }

    [Fact]
    public async Task SavedFile_HasTheVersionedEnvelope()
    {
        using var store = CreateRouted();
        await store.TrustAsync(new SshRoute(BastionA, PrivateTarget), Identity(FingerprintA));

        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(RoutesPath));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var entry = Assert.Single(root.GetProperty("entries").EnumerateArray().ToArray());
        Assert.Equal("bastion-a.example.test", entry.GetProperty("via").GetProperty("host").GetString());
        Assert.Equal("10.0.0.5", entry.GetProperty("endpoint").GetProperty("host").GetString());
        Assert.True(entry.TryGetProperty("identity", out _));
        Assert.True(entry.TryGetProperty("confirmedAt", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static HostKeyIdentity Identity(string fingerprint) => HostKeyIdentity.Create("ssh-ed25519", fingerprint);

    private JsonHostKeyTrustStore CreateDirect() => new(
        new HostKeyTrustStorageOptions { FilePath = DirectPath },
        NullLogger<JsonHostKeyTrustStore>.Instance);

    private JsonRoutedHostKeyTrustStore CreateRouted() => new(
        RoutedHostKeyTrustStorageOptions.From(new HostKeyTrustStorageOptions { FilePath = DirectPath }),
        NullLogger<JsonRoutedHostKeyTrustStore>.Instance);
}
