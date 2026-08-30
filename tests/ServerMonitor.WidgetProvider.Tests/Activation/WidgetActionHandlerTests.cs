using ServerMonitor.ActivationContract;
using ServerMonitor.WidgetProvider.Activation;

namespace ServerMonitor.WidgetProvider.Tests.Activation;

public sealed class WidgetActionHandlerTests
{
    private sealed class FakeAppLauncher : IAppLauncher
    {
        public List<string> Launched { get; } = new();

        public bool ThrowOnLaunch { get; set; }

        public void Launch(string uri)
        {
            if (ThrowOnLaunch)
            {
                throw new InvalidOperationException("launch failed");
            }

            Launched.Add(uri);
        }
    }

    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Open_dashboard_launches_dashboard_uri()
    {
        var launcher = new FakeAppLauncher();
        new WidgetActionHandler(launcher).Handle(ActivationVerbs.OpenDashboard, null);
        Assert.Equal("serveralyzer://dashboard", Assert.Single(launcher.Launched));
    }

    [Fact]
    public void Open_server_launches_server_uri_from_data()
    {
        var launcher = new FakeAppLauncher();
        new WidgetActionHandler(launcher).Handle(ActivationVerbs.OpenServer, $"{{\"serverId\":\"{Id:D}\"}}");
        Assert.Equal($"serveralyzer://server/{Id:D}", Assert.Single(launcher.Launched));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("openurl")]
    [InlineData("run")]
    public void Unknown_verb_launches_nothing(string? verb)
    {
        var launcher = new FakeAppLauncher();
        new WidgetActionHandler(launcher).Handle(verb, null);
        Assert.Empty(launcher.Launched);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"serverId\":\"not-a-guid\"}")]
    [InlineData("{\"serverId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"other\":\"x\"}")]
    [InlineData("{\"serverId\":123}")]
    public void Open_server_with_bad_data_launches_nothing(string? data)
    {
        var launcher = new FakeAppLauncher();
        new WidgetActionHandler(launcher).Handle(ActivationVerbs.OpenServer, data);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public void Launch_failure_is_contained()
    {
        var launcher = new FakeAppLauncher { ThrowOnLaunch = true };
        // Must not throw — a launch failure is logged and swallowed.
        new WidgetActionHandler(launcher).Handle(ActivationVerbs.OpenDashboard, null);
    }

    [Fact]
    public void Data_only_reads_serverId_ignores_extra_fields()
    {
        var launcher = new FakeAppLauncher();
        new WidgetActionHandler(launcher).Handle(
            ActivationVerbs.OpenServer, $"{{\"serverId\":\"{Id:D}\",\"cmd\":\"rm -rf\",\"url\":\"http://x\"}}");
        Assert.Equal($"serveralyzer://server/{Id:D}", Assert.Single(launcher.Launched));
    }
}
