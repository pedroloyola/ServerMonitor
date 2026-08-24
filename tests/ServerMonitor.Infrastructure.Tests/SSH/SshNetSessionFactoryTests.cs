using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.Infrastructure.Tests.SSH;

public sealed class SshNetSessionFactoryTests
{
    [Fact]
    public void Invalid_private_key_is_reported_as_typed_load_failure()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not a private key");
            var factory = new SshNetSessionFactory();

            Assert.Throws<SshPrivateKeyLoadException>(() => factory.CreatePrivateKeySession(
                Server(path),
                path,
                passphrase: null,
                TimeSpan.FromSeconds(1)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Oversized_private_key_is_rejected_before_parsing()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[(1024 * 1024) + 1]);
            var factory = new SshNetSessionFactory();

            Assert.Throws<SshPrivateKeyLoadException>(() => factory.CreatePrivateKeySession(
                Server(path),
                path,
                passphrase: null,
                TimeSpan.FromSeconds(1)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Server Server(string path) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test",
        Host = "server.example",
        Port = 22,
        Username = "tester",
        AuthenticationMethod = AuthenticationMethod.SshKey,
        PrivateKeyPath = path,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
