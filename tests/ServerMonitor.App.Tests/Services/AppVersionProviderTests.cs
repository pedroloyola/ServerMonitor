using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Services;

public sealed class AppVersionProviderTests
{
    [Fact]
    public void Resolve_Packaged_UsesPackageIdentityVersion()
    {
        var (version, packaged) = AppVersionProvider.Resolve(
            () => new Version(1, 2, 3, 4),
            () => new Version(9, 9, 9, 9));

        Assert.Equal("1.2.3", version);
        Assert.True(packaged);
    }

    [Fact]
    public void Resolve_Unpackaged_FallsBackToAssemblyVersion()
    {
        // Package.Current throws when unpackaged; resolution must not crash (§104).
        var (version, packaged) = AppVersionProvider.Resolve(
            () => throw new InvalidOperationException("no package identity"),
            () => new Version(1, 0, 0, 0));

        Assert.Equal("1.0.0", version);
        Assert.False(packaged);
    }

    [Fact]
    public void Resolve_BothUnavailable_ReturnsZeroVersionWithoutThrowing()
    {
        var (version, packaged) = AppVersionProvider.Resolve(
            () => throw new InvalidOperationException(),
            () => null);

        Assert.Equal("0.0.0", version);
        Assert.False(packaged);
    }

    [Fact]
    public void Format_DropsRevisionAndClampsNegative()
    {
        Assert.Equal("1.0.0", AppVersionProvider.Format(new Version(1, 0, 0, 7)));
        Assert.Equal("2.5.9", AppVersionProvider.Format(new Version(2, 5, 9)));
    }

    [Fact]
    public void Provider_Instance_ExposesVersionAndPackagedFlag()
    {
        var provider = new AppVersionProvider(
            packageVersion: () => new Version(3, 1, 4, 0),
            assemblyVersion: () => new Version(0, 0, 0, 0));

        Assert.Equal("3.1.4", provider.DisplayVersion);
        Assert.True(provider.IsPackaged);
    }
}
