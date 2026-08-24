using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Tests.Models;

public sealed class ServerDefinitionTests
{
    [Fact]
    public void Defaults_AreSafeAndMatchBootstrapContract()
    {
        var server = new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Servidor de teste",
            Host = "server.example.test",
            Username = "monitor"
        };

        Assert.Equal(22, server.Port);
        Assert.Equal(TimeSpan.FromSeconds(30), server.RefreshInterval);
        Assert.Equal(ServerOperatingSystem.Unknown, server.OperatingSystem);
        Assert.Equal(AuthenticationMethod.SshKey, server.AuthenticationMethod);
        Assert.True(server.IsEnabled);
    }
}
