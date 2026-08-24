using Renci.SshNet;
using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.Infrastructure.Tests.SSH;

public sealed class SshModernAlgorithmPolicyTests
{
    [Fact]
    public void Apply_removes_legacy_algorithms()
    {
        var connectionInfo = new ConnectionInfo(
            "server.example",
            22,
            "tester",
            new NoneAuthenticationMethod("tester"));

        SshModernAlgorithmPolicy.Apply(connectionInfo);

        Assert.DoesNotContain(connectionInfo.KeyExchangeAlgorithms.Keys, name => name.Contains("sha1", StringComparison.Ordinal));
        Assert.DoesNotContain(connectionInfo.Encryptions.Keys, name => name.Contains("cbc", StringComparison.Ordinal));
        Assert.DoesNotContain(connectionInfo.Encryptions.Keys, name => name.Contains("3des", StringComparison.Ordinal));
        Assert.DoesNotContain(connectionInfo.HmacAlgorithms.Keys, name => name.Contains("sha1", StringComparison.Ordinal));
        Assert.DoesNotContain("ssh-rsa", connectionInfo.HostKeyAlgorithms.Keys);
    }

    [Fact]
    public void Apply_retains_only_explicitly_allowed_algorithms()
    {
        var connectionInfo = new ConnectionInfo(
            "server.example",
            22,
            "tester",
            new NoneAuthenticationMethod("tester"));

        SshModernAlgorithmPolicy.Apply(connectionInfo);

        Assert.All(connectionInfo.KeyExchangeAlgorithms.Keys, name => Assert.Contains(name, SshModernAlgorithmPolicy.KeyExchangeAlgorithms));
        Assert.All(connectionInfo.Encryptions.Keys, name => Assert.Contains(name, SshModernAlgorithmPolicy.EncryptionAlgorithms));
        Assert.All(connectionInfo.HmacAlgorithms.Keys, name => Assert.Contains(name, SshModernAlgorithmPolicy.HmacAlgorithms));
        Assert.All(connectionInfo.HostKeyAlgorithms.Keys, name => Assert.Contains(name, SshModernAlgorithmPolicy.HostKeyAlgorithms));
    }
}
