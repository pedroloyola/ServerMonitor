using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Qa;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Backup;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>The Debug-only --qa-backup harness: off by default, and it only swaps the engine and its pickers.</summary>
public sealed class QaBackupHarnessTests
{
    [Fact]
    public void HarnessIsNotRequestedByDefault()
    {
        Assert.Null(QaBackupScenarioComposition.RequestedScenario());
    }

    [Fact]
    public void WithoutTheFlag_TheCompositionUsesTheRealPickersAndDialogs()
    {
        var services = new ServiceCollection();
        App.ConfigureApplicationServices(services);

        Assert.Equal(typeof(BackupFilePicker), Last<IBackupFilePicker>(services).ImplementationType);
        Assert.Equal(typeof(BackupRestoreDialogService), Last<IBackupRestoreInteraction>(services).ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, Last<BackupRestoreViewModel>(services).Lifetime);
        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationInstance is QaBackupService);
    }

    [Fact]
    public void Apply_ReplacesOnlyTheEngineAndThePickers()
    {
        var services = new ServiceCollection();
        App.ConfigureApplicationServices(services);
        var before = services.Count;

        QaBackupScenarioComposition.Apply(services, "ok");

        Assert.Equal(before + 2, services.Count);
        Assert.IsType<QaBackupService>(Last<IConfigurationBackupService>(services).ImplementationInstance);
        Assert.Equal(typeof(QaBackupFilePicker), Last<IBackupFilePicker>(services).ImplementationType);
    }

    /// <summary>The double opens no dialog and touches no file: both picks are plain paths under temp.</summary>
    [Fact]
    public async Task ThePickerDoubleNeverTouchesTheFileSystem()
    {
        var picker = new QaBackupFilePicker();

        var save = await picker.PickSaveAsync("ServerAlyzer-backup-2026-09-30.serveralyzer-backup", "label");
        var open = await picker.PickOpenAsync();

        Assert.StartsWith(Path.GetTempPath(), save);
        Assert.StartsWith(Path.GetTempPath(), open);
        Assert.False(File.Exists(save));
        Assert.False(File.Exists(open));
    }

    private static ServiceDescriptor Last<TService>(IServiceCollection services) =>
        services.Last(descriptor => descriptor.ServiceType == typeof(TService));
}
