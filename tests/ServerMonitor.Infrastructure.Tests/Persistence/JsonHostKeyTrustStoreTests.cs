using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.Infrastructure.Tests.Persistence;

public sealed class JsonHostKeyTrustStoreTests : IDisposable
{
    private const string Fingerprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ServerMonitor.Trust.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Trust_RoundTripsAcrossRestart()
    {
        var path = Path.Combine(_directory, "known-hosts.json");
        var endpoint = SshEndpoint.Create("Example.COM", 22);
        var identity = HostKeyIdentity.Create("ssh-ed25519", Fingerprint);

        using (var first = Create(path))
        {
            await first.TrustAsync(endpoint, identity);
        }

        using var restarted = Create(path);
        var restored = await restarted.GetAsync(SshEndpoint.Create("example.com.", 22));
        Assert.NotNull(restored);
        Assert.True(restored.Identity.Matches(identity));
    }

    [Fact]
    public async Task Trust_DoesNotReplaceMismatch()
    {
        var path = Path.Combine(_directory, "known-hosts.json");
        var endpoint = SshEndpoint.Create("server.example.test", 22);
        using var store = Create(path);
        await store.TrustAsync(endpoint, HostKeyIdentity.Create("ssh-ed25519", Fingerprint));

        await Assert.ThrowsAsync<HostKeyTrustConflictException>(() => store.TrustAsync(
            endpoint,
            HostKeyIdentity.Create("ssh-ed25519", "AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")));
    }

    [Fact]
    public async Task Remove_DeletesTrustedIdentity()
    {
        var path = Path.Combine(_directory, "known-hosts.json");
        var endpoint = SshEndpoint.Create("server.example.test", 22);
        using var store = Create(path);
        await store.TrustAsync(endpoint, HostKeyIdentity.Create("ssh-ed25519", Fingerprint));

        Assert.True(await store.RemoveAsync(endpoint));
        Assert.Null(await store.GetAsync(endpoint));
    }

    [Fact]
    public async Task Get_MalformedTrustFile_FailsClosedOnEveryRead()
    {
        var path = Path.Combine(_directory, "known-hosts.json");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(path, "{ not-json }");
        using var store = Create(path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.GetAsync(SshEndpoint.Create("server.example.test", 22)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.GetAsync(SshEndpoint.Create("server.example.test", 22)));
    }

    [Fact]
    public async Task Trust_InvalidPersistedEntry_DoesNotOverwriteTrustFile()
    {
        var path = Path.Combine(_directory, "known-hosts.json");
        Directory.CreateDirectory(_directory);
        const string invalidTrust = """
            [
              {
                "endpoint": { "host": "server.example.test", "port": 22 },
                "identity": { "algorithm": "ssh-ed25519", "sha256Fingerprint": "invalid" },
                "confirmedAt": "2026-08-24T00:00:00+00:00"
              }
            ]
            """;
        await File.WriteAllTextAsync(path, invalidTrust);
        using var store = Create(path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.TrustAsync(
            SshEndpoint.Create("other.example.test", 22),
            HostKeyIdentity.Create("ssh-ed25519", Fingerprint)));

        Assert.Equal(invalidTrust, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("""
        [
          {
            "endpoint": { "host": "server.example.test", "port": 22 },
            "identity": { "algorithm": "ssh-ed25519", "sha256Fingerprint": "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" },
            "confirmedAt": "2026-08-24T00:00:00+00:00"
          },
          {
            "endpoint": { "host": "SERVER.EXAMPLE.TEST.", "port": 22 },
            "identity": { "algorithm": "ssh-ed25519", "sha256Fingerprint": "SHA256:AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" },
            "confirmedAt": "2026-08-24T00:00:00+00:00"
          }
        ]
        """)]
    public async Task Get_NullOrDuplicateTrustEntry_FailsClosed(string trustJson)
    {
        var path = Path.Combine(_directory, "known-hosts.json");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(path, trustJson);
        using var store = Create(path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.GetAsync(SshEndpoint.Create("server.example.test", 22)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static JsonHostKeyTrustStore Create(string path) => new(
        new HostKeyTrustStorageOptions { FilePath = path },
        NullLogger<JsonHostKeyTrustStore>.Instance);
}
