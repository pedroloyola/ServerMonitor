using ServerMonitor.Core.Alerts;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Tests.Alerts;

public sealed class ServerAlertPolicyTests
{
    [Theory]
    [InlineData(ServerHealth.Healthy)]
    [InlineData(ServerHealth.Warning)]
    [InlineData(ServerHealth.Critical)]
    [InlineData(ServerHealth.Offline)]
    [InlineData(ServerHealth.Unknown)]
    public void SameHealth_DoesNotAlert(ServerHealth health) =>
        Assert.Null(ServerAlertPolicy.Evaluate(health, health));

    [Theory]
    [InlineData(ServerHealth.Healthy, ServerHealth.Warning, ServerAlertCategory.Warning)]
    [InlineData(ServerHealth.Healthy, ServerHealth.Critical, ServerAlertCategory.Critical)]
    [InlineData(ServerHealth.Warning, ServerHealth.Critical, ServerAlertCategory.Critical)]
    [InlineData(ServerHealth.Healthy, ServerHealth.Offline, ServerAlertCategory.Offline)]
    [InlineData(ServerHealth.Warning, ServerHealth.Offline, ServerAlertCategory.Offline)]
    [InlineData(ServerHealth.Critical, ServerHealth.Offline, ServerAlertCategory.Offline)]
    [InlineData(ServerHealth.Offline, ServerHealth.Healthy, ServerAlertCategory.Recovery)]
    [InlineData(ServerHealth.Offline, ServerHealth.Warning, ServerAlertCategory.Recovery)]
    [InlineData(ServerHealth.Offline, ServerHealth.Critical, ServerAlertCategory.Recovery)]
    [InlineData(ServerHealth.Warning, ServerHealth.Healthy, ServerAlertCategory.Recovery)]
    [InlineData(ServerHealth.Critical, ServerHealth.Healthy, ServerAlertCategory.Recovery)]
    public void AlertingTransition_ReturnsExpectedCategory(
        ServerHealth previous,
        ServerHealth current,
        ServerAlertCategory expected)
    {
        var result = ServerAlertPolicy.Evaluate(previous, current);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Category);
        Assert.Equal(previous, result.PreviousHealth);
        Assert.Equal(current, result.CurrentHealth);
    }

    [Theory]
    [InlineData(ServerHealth.Critical, ServerHealth.Warning)]
    [InlineData(ServerHealth.Unknown, ServerHealth.Healthy)]
    [InlineData(ServerHealth.Unknown, ServerHealth.Warning)]
    [InlineData(ServerHealth.Unknown, ServerHealth.Critical)]
    [InlineData(ServerHealth.Unknown, ServerHealth.Offline)]
    [InlineData(ServerHealth.Healthy, ServerHealth.Unknown)]
    [InlineData(ServerHealth.Offline, ServerHealth.Unknown)]
    public void SilentTransition_DoesNotAlert(ServerHealth previous, ServerHealth current) =>
        Assert.Null(ServerAlertPolicy.Evaluate(previous, current));
}
