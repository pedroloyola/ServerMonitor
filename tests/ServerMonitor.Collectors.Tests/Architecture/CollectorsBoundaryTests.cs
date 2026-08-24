namespace ServerMonitor.Collectors.Tests.Architecture;

public sealed class CollectorsBoundaryTests
{
    [Fact]
    public void AssemblyMarker_BelongsToCollectorsAssembly()
    {
        Assert.Equal(
            "ServerMonitor.Collectors",
            ServerMonitor.Collectors.CollectorsAssembly.Marker.Assembly.GetName().Name);
    }
}
