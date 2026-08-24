using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Tests.Domain;

public sealed class SshIdentityTests
{
    private const string FingerprintPayload = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Theory]
    [InlineData("Example.COM.", 22, "example.com")]
    [InlineData("127.0.0.1", 2222, "127.0.0.1")]
    [InlineData("2001:0db8::1", 22, "2001:db8::1")]
    public void Endpoint_NormalizesHost(string host, int port, string expectedHost)
    {
        var endpoint = SshEndpoint.Create(host, port);

        Assert.Equal(expectedHost, endpoint.Host);
        Assert.Equal(port, endpoint.Port);
    }

    [Fact]
    public void Fingerprint_CanonicalizesSha256()
    {
        var identity = HostKeyIdentity.Create("ssh-ed25519", $" sha256:{FingerprintPayload}= ");

        Assert.Equal($"SHA256:{FingerprintPayload}", identity.Sha256Fingerprint);
    }

    [Fact]
    public void Fingerprint_UsesIdentityAndRejectsMismatch()
    {
        var trusted = HostKeyIdentity.Create("ssh-ed25519", FingerprintPayload);
        var same = HostKeyIdentity.Create("ssh-ed25519", FingerprintPayload);
        var differentAlgorithm = HostKeyIdentity.Create("rsa-sha2-512", FingerprintPayload);
        var differentFingerprint = HostKeyIdentity.Create(
            "ssh-ed25519",
            "AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        Assert.True(trusted.Matches(same));
        Assert.False(trusted.Matches(differentAlgorithm));
        Assert.False(trusted.Matches(differentFingerprint));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AQID")]
    public void Fingerprint_RejectsInvalidData(string value)
    {
        Assert.Throws<ArgumentException>(() => HostKeyIdentity.Create("ssh-ed25519", value));
    }
}
