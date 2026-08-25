using ServerMonitor.Core.Discovery;

namespace ServerMonitor.Core.Tests.Discovery;

public sealed class ServiceInstanceIdentityTests
{
    [Fact]
    public void CaseAndTrailingDotVariants_AreEqualAndHaveStableHash()
    {
        var first = ServiceInstanceIdentity.TryCreate("Mac Studio.", "_SSH._TCP.", "LOCAL.");
        var second = ServiceInstanceIdentity.TryCreate("mac studio", "_ssh._tcp", "local");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(first.StableHash, second.StableHash);
        Assert.True(DiscoveryInputPolicy.IsValidIdentityHash(first.StableHash));
    }

    [Fact]
    public void DistinctInstanceNames_OnSameServiceRemainDistinct()
    {
        var admin = ServiceInstanceIdentity.TryCreate("Admin SSH", "_ssh._tcp", "local");
        var maintenance = ServiceInstanceIdentity.TryCreate("Maintenance SSH", "_ssh._tcp", "local");

        Assert.NotEqual(admin, maintenance);
        Assert.NotEqual(admin!.StableHash, maintenance!.StableHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0ABC")]
    [InlineData("abcdef")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void InvalidIdentityHash_IsRejected(string? value) =>
        Assert.False(DiscoveryInputPolicy.IsValidIdentityHash(value));
}
