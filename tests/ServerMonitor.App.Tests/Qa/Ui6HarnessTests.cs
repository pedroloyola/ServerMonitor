using ServerMonitor.App.Qa;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Tests.Qa;

public sealed class Ui6HarnessTests
{
    private static string[] Args(string flag) => ["app", "--qa-overview", "--qa-backup=ok", flag];

    [Theory]
    [InlineData("--qa-start=overview")]
    [InlineData("--qa-start=servers")]
    [InlineData("--qa-start=history")]
    [InlineData("--qa-start=settings")]
    [InlineData("--qa-start=settings-data")]
    [InlineData("--qa-start=detail:1")]
    [InlineData("--qa-activation=dashboard")]
    [InlineData("--qa-activation=server:1")]
    public void Flags_RequireIsolatedOverview(string flag)
    {
        Assert.Null(QaShellStartup.Refusal(Args(flag)));
        Assert.Null(QaStartupIsolation.LaunchRefusal(Args(flag)));
        Assert.NotNull(QaStartupIsolation.LaunchRefusal(["app", flag]));
        Assert.NotNull(QaStartupIsolation.LaunchRefusal(["app", "--qa-history", flag]));
    }

    [Theory]
    [InlineData("--qa-start=detail:0")]
    [InlineData("--qa-start=detail:999")]
    [InlineData("--qa-start=detail:+1")]
    [InlineData("--qa-start=bogus")]
    [InlineData("--qa-start=")]
    [InlineData("--qa-start")]
    [InlineData("--qa-activation=server:0")]
    [InlineData("--qa-activation=server:-1")]
    [InlineData("--qa-activation=server:999")]
    [InlineData("--qa-activation=bogus")]
    [InlineData("--qa-activation")]
    public void MalformedModifiers_AreRefused(string flag) => Assert.NotNull(QaStartupIsolation.LaunchRefusal(Args(flag)));

    [Fact]
    public void DuplicateAndConflictingModifiers_AreRefused()
    {
        Assert.NotNull(QaShellStartup.Refusal([.. Args("--qa-start=overview"), "--qa-start=servers"]));
        Assert.NotNull(QaShellStartup.Refusal([.. Args("--qa-start=overview"), "--qa-activation=dashboard"]));
    }

    [Fact]
    public void Activation_UsesSyntheticOpaqueId_ThroughColdHandOff()
    {
        var intent = QaShellStartup.Activation(Args("--qa-activation=server:1"));
        Assert.NotNull(intent);
        Assert.Equal(QaOverviewCatalog.Build("mixed").Servers[0].Server.Id, intent.ServerId);
        var pending = new PendingActivation(); pending.Deliver(intent);
        ServerMonitor.ActivationContract.ActivationIntent? received = null;
        pending.Attach(value => received = value);
        Assert.Same(intent, received);
    }

    [Theory]
    [InlineData("first-run", ServerLoadStatus.NotFound, 0)]
    [InlineData("empty", ServerLoadStatus.Loaded, 0)]
    [InlineData("all-hidden", ServerLoadStatus.Loaded, 1)]
    [InlineData("config-unavailable", ServerLoadStatus.Unavailable, 0)]
    public async Task Scenarios_AreInMemoryAndHaveDistinctStatus(string name, ServerLoadStatus status, int count)
    {
        Assert.Contains(name, QaOverviewScenarioPolicy.Scenarios);
        var service = new QaOverviewServerService(QaOverviewCatalog.Build(name));
        Assert.Equal(status, await service.GetLoadStatusAsync());
        var servers = await service.GetAllAsync(); Assert.Equal(count, servers.Count);
        if (name == "all-hidden") Assert.All(servers, s => Assert.True(s.IsHidden));
    }
}
