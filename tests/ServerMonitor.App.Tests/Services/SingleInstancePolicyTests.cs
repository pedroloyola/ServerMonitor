using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Services;

public sealed class SingleInstancePolicyTests
{
    [Fact]
    public void ResolveInstanceKey_ProductionLaunch_ReturnsStableProductKey()
    {
        var key = SingleInstancePolicy.ResolveInstanceKey(
            new[] { "ServerMonitor.exe" }, isDebugBuild: false);

        Assert.Equal("ServerMonitor", key);
        Assert.DoesNotContain("pedro", key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveInstanceKey_ReleaseIgnoresQaArgument_StillSingleInstanced()
    {
        // In a Release build a --qa- argument must NEVER bypass single-instancing.
        var key = SingleInstancePolicy.ResolveInstanceKey(
            new[] { "ServerMonitor.exe", "--qa-health" }, isDebugBuild: false);

        Assert.Equal("ServerMonitor", key);
    }

    [Fact]
    public void ResolveInstanceKey_DebugWithoutQaArgument_IsSingleInstanced()
    {
        var key = SingleInstancePolicy.ResolveInstanceKey(
            new[] { "ServerMonitor.exe" }, isDebugBuild: true);

        Assert.Equal("ServerMonitor", key);
    }

    [Theory]
    [InlineData("--qa-health")]
    [InlineData("--qa-discovery")]
    [InlineData("--qa-compact")]
    [InlineData("--qa-history")]
    [InlineData("--qa-workloads")]
    [InlineData("--QA-Health")]
    public void ResolveInstanceKey_DebugQaHarness_BypassesSingleInstancing(string qaArgument)
    {
        var key = SingleInstancePolicy.ResolveInstanceKey(
            new[] { "ServerMonitor.exe", qaArgument }, isDebugBuild: true);

        Assert.Null(key);
    }

    [Fact]
    public void HasQaArgument_DetectsQaFlagAnywhere()
    {
        Assert.True(SingleInstancePolicy.HasQaArgument(new[] { "app.exe", "--qa-compact" }));
        Assert.False(SingleInstancePolicy.HasQaArgument(new[] { "app.exe", "--verbose" }));
        Assert.False(SingleInstancePolicy.HasQaArgument(Array.Empty<string>()));
    }
}
