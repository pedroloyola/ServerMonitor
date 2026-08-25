using ServerMonitor.App.Qa;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.Qa;

public sealed class QaNotificationHarnessTests
{
    [Fact]
    public void HarnessIsNotRequestedByDefault() =>
        Assert.False(QaNotificationComposition.IsRequested());

    [Fact]
    public void SequenceCoversWarningCriticalRecoveryOfflineAndRecovery()
    {
        Assert.Equal(
            [
                ServerHealth.Warning,
                ServerHealth.Critical,
                ServerHealth.Healthy,
                ServerHealth.Offline,
                ServerHealth.Healthy
            ],
            QaNotificationSequenceService.Sequence);
    }

    [Fact]
    public async Task ServerServiceIsInMemoryAndStable()
    {
        var service = new QaNotificationServerService();

        var first = Assert.Single(await service.GetAllAsync());
        var second = Assert.Single(await service.GetAllAsync());

        Assert.Equal(QaNotificationServerService.ServerId, first.Id);
        Assert.Equal(first, second);
        Assert.EndsWith(".invalid", first.Host, StringComparison.Ordinal);
    }
}
