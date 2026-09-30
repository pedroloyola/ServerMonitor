using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Tests.Backup;

public sealed class ServerNormalizerTests
{
    [Fact]
    public void Normalize_Server_TrimsText_CanonicalizesPaths_SnapsInterval_IncludingJump()
    {
        var server = new Server
        {
            Id = Guid.NewGuid(),
            Name = "  web  ",
            Host = " 10.0.0.1 ",
            Username = " ops ",
            PrivateKeyPath = @"  C:\keys\..\keys\id_ed25519  ",
            RefreshIntervalSeconds = 7,
            Route = new ServerRoute
            {
                Jump = new JumpHop { Host = " bastion ", Username = " jump ", PrivateKeyPath = @" C:\k\.\j " }
            }
        };

        var normalized = ServerNormalizer.Normalize(server);

        Assert.Equal("web", normalized.Name);
        Assert.Equal("10.0.0.1", normalized.Host);
        Assert.Equal("ops", normalized.Username);
        Assert.Equal(@"C:\keys\id_ed25519", normalized.PrivateKeyPath);
        Assert.Equal(10, normalized.RefreshIntervalSeconds);
        Assert.Equal("bastion", normalized.Route!.Jump!.Host);
        Assert.Equal("jump", normalized.Route.Jump.Username);
        Assert.Equal(@"C:\k\j", normalized.Route.Jump.PrivateKeyPath);
        Assert.Equal(normalized, ServerNormalizer.Normalize(normalized));
    }

    [Fact]
    public void Normalize_ServerInput_MatchesTheServerOverload()
    {
        var input = new ServerInput { Name = " a ", Host = " h ", Username = " u ", PrivateKeyPath = "   ", RefreshIntervalSeconds = 0 };

        var normalized = ServerNormalizer.Normalize(input);

        Assert.Equal("a", normalized.Name);
        Assert.Null(normalized.PrivateKeyPath);
        Assert.Equal(30, normalized.RefreshIntervalSeconds);
    }

    [Theory]
    [InlineData(@"C:\keys\id", true)]
    [InlineData(@"key", false)]
    [InlineData(@"C:key", false)]
    [InlineData(@"C:\keys\..\id", false)]
    [InlineData(@"\\host\share\k", true)] // canonical UNC: allowed here, handled by the probe (never probed)
    [InlineData(@"C:\keys\id ", false)]
    public void IsCanonicalKeyPath(string path, bool expected)
    {
        Assert.Equal(expected, ServerNormalizer.IsCanonicalKeyPath(path));
    }
}

public sealed class TrustReferenceSetTests
{
    [Fact]
    public void Direct_ReferencesItsOwnEndpoint_Normalized()
    {
        var set = TrustReferenceSet.From([Server("Web.Example.COM.", 22)]);

        Assert.Equal([new SshEndpoint("web.example.com", 22)], set.Direct);
        Assert.Empty(set.Routed);
    }

    [Fact]
    public void Routed_ReferencesItsJumpAsDirect_AndTheRoute_NeverItsTargetAsDirect()
    {
        var routed = Server("10.0.0.5", 2222) with
        {
            Route = new ServerRoute { Jump = new JumpHop { Host = "Bastion.", Port = 22, Username = "j" } }
        };

        var set = TrustReferenceSet.From([routed]);

        var via = new SshEndpoint("bastion", 22);
        Assert.Equal([via], set.Direct);
        Assert.Equal([new SshRoute(via, new SshEndpoint("10.0.0.5", 2222))], set.Routed);
    }

    // Vigil N1: a host the connect path rejects contributes nothing and never throws.
    [Theory]
    [InlineData("xn--a")]          // invalid IDN label: SshEndpoint.Create throws
    [InlineData("   ")]
    public void EndpointTheConnectPathRejects_ContributesNothing(string host)
    {
        var set = TrustReferenceSet.From([Server(host, 22)]);

        Assert.Empty(set.Direct);
    }

    [Fact]
    public void RoutedWithInvalidJump_ContributesNothing_LikeTheConnectPath()
    {
        var routed = Server("10.0.0.5", 22) with
        {
            Route = new ServerRoute { Jump = new JumpHop { Host = "bastion", Port = 22, Username = " " } }
        };

        var set = TrustReferenceSet.From([routed]);

        Assert.Empty(set.Direct);
        Assert.Empty(set.Routed);
    }

    [Fact]
    public void BlankUsername_ContributesNothing()
    {
        Assert.Empty(TrustReferenceSet.From([Server("host", 22) with { Username = "" }]).Direct);
    }

    private static Server Server(string host, int port) => new()
    {
        Id = Guid.NewGuid(),
        Name = "s",
        Host = host,
        Port = port,
        Username = "u",
        AuthenticationMethod = AuthenticationMethod.Password,
        CredentialReferenceId = Guid.NewGuid()
    };
}
