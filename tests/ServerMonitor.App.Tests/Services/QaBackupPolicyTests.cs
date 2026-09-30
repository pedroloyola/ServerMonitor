using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Services;

public sealed class QaBackupPolicyTests
{
    [Theory]
    [InlineData("ok")]
    [InlineData("rollback")]
    [InlineData("partial")]
    [InlineData("invalid")]
    [InlineData("stuck")]
    [InlineData("recovered")]
    public void A_known_scenario_is_resolved_in_debug_in_both_flag_forms(string scenario)
    {
        Assert.Equal(scenario, QaBackupPolicy.ResolveScenario(["app.exe", "--qa-backup", scenario], isDebugBuild: true));
        Assert.Equal(
            scenario,
            QaBackupPolicy.ResolveScenario(["app.exe", "--QA-BACKUP=" + scenario.ToUpperInvariant()], isDebugBuild: true));
    }

    [Fact]
    public void Release_always_ignores_the_flag()
    {
        Assert.Null(QaBackupPolicy.ResolveScenario(["app.exe", "--qa-backup", "ok"], isDebugBuild: false));
    }

    [Theory]
    [InlineData("app.exe")]
    [InlineData("app.exe", "--qa-backup")]
    [InlineData("app.exe", "--qa-backup", "everything")]
    [InlineData("app.exe", "--qa-backup=")]
    public void No_flag_or_an_unknown_scenario_never_falls_back_to_a_default(params string[] arguments)
    {
        Assert.Null(QaBackupPolicy.ResolveScenario(arguments, isDebugBuild: true));
    }
}
