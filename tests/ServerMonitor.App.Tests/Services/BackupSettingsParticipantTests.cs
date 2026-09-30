using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Infrastructure.Backup;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.App.Tests.Services;

// M14.6: the App's settings writers under the restore gate, the portable-settings participant, the journal's
// temp clean-up and the composition root.
public sealed class BackupSettingsParticipantTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sm-backup-settings-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigurationWriteGate _gate = new();

    public BackupSettingsParticipantTests()
    {
        Directory.CreateDirectory(_directory);
    }

    private string NotificationPath => Path.Combine(_directory, "notification-settings.json");

    private string BackgroundPath => Path.Combine(_directory, "background-settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task SettingsSetters_AreRefusedWhileLocked_AndNothingIsWritten()
    {
        var (notifications, background) = CreateServices();
        var token = (await _gate.BeginRestoreAsync(TimeSpan.FromSeconds(1)))!;

        Assert.Throws<ConfigurationLockedException>(() => notifications.SetNotificationsEnabled(false));
        Assert.Throws<ConfigurationLockedException>(() => background.SetBackgroundMonitoringEnabled(false));

        Assert.False(File.Exists(NotificationPath));
        Assert.False(File.Exists(BackgroundPath));
        Assert.True(notifications.NotificationsEnabled);
        Assert.True(background.BackgroundMonitoringEnabled);

        _gate.Release(token);
        notifications.SetNotificationsEnabled(false);
        Assert.False(notifications.NotificationsEnabled);
    }

    [Fact]
    public async Task BackgroundNotice_WhileLocked_IsClaimedInMemoryOnly()
    {
        var (_, background) = CreateServices();
        var token = (await _gate.BeginRestoreAsync(TimeSpan.FromSeconds(1)))!;
        _gate.Seal(token);

        Assert.True(background.TryClaimBackgroundNotice());

        Assert.True(background.BackgroundNoticeShown);
        Assert.False(File.Exists(BackgroundPath));
        Assert.False(background.TryClaimBackgroundNotice());
    }

    [Fact]
    public void Render_IsExactlyWhatANormalSaveWrites()
    {
        var (notifications, background) = CreateServices();

        notifications.SetNotificationsEnabled(false);
        background.SetBackgroundMonitoringEnabled(false);

        Assert.Equal(JsonNotificationSettingsService.Render(false), File.ReadAllBytes(NotificationPath));
        Assert.Equal(JsonBackgroundMonitoringSettingsService.Render(false, false), File.ReadAllBytes(BackgroundPath));
    }

    [Fact]
    public async Task Participant_Restore_ReplacesTheFiles_ReloadsState_AndKeepsTheTargetsNoticeFlag()
    {
        var (notifications, background) = CreateServices();
        Assert.True(background.TryClaimBackgroundNotice());
        var participant = new PortableSettingsParticipant(
            new NotificationSettingsStorageOptions { FilePath = NotificationPath },
            new BackgroundSettingsStorageOptions { FilePath = BackgroundPath },
            notifications,
            background);
        var restored = new PortableSettings(NotificationsEnabled: false, BackgroundMonitoringEnabled: false);
        var token = (await _gate.BeginRestoreAsync(TimeSpan.FromSeconds(1)))!;

        var notificationBytes = participant.RenderNotificationSettings(restored);
        var backgroundBytes = participant.RenderBackgroundSettings(restored);
        participant.ReplaceNotificationSettings(token, notificationBytes);
        participant.ReplaceBackgroundSettings(token, backgroundBytes);

        Assert.Equal(restored, participant.ReadCurrent());
        Assert.Equal(restored, participant.ReadNotificationAndBackground(File.ReadAllBytes(NotificationPath), File.ReadAllBytes(BackgroundPath)));
        Assert.True(background.BackgroundNoticeShown);

        // Rollback path: "absent before" deletes the file and the defaults come back.
        participant.ReplaceNotificationSettings(token, null);
        Assert.False(File.Exists(NotificationPath));
        Assert.True(notifications.NotificationsEnabled);
    }

    [Fact]
    public async Task Participant_RestoreWrite_RequiresTheGateToken()
    {
        var (notifications, _) = CreateServices();
        var token = (await _gate.BeginRestoreAsync(TimeSpan.FromSeconds(1)))!;
        _gate.Release(token);

        Assert.Throws<InvalidOperationException>(() => notifications.ReplaceForRestore(token, JsonNotificationSettingsService.Render(false)));
        Assert.False(File.Exists(NotificationPath));
    }

    [Fact]
    public void JournalTemporaryCleanup_RemovesExactlyTheJournalTemps()
    {
        var journal = Path.Combine(_directory, "restore-journal");
        Directory.CreateDirectory(Path.Combine(journal, "pre"));
        var temps = RestoreJournalRecovery.JournalTemporaryFiles(journal);
        foreach (var temp in temps)
        {
            File.WriteAllText(temp, "x");
        }

        var keep = Path.Combine(journal, "journal.json");
        var unrelated = Path.Combine(journal, "pre", "user.tmp");
        File.WriteAllText(keep, "{}");
        File.WriteAllText(unrelated, "x");

        new OrphanTemporaryCleaner(NullLogger<OrphanTemporaryCleaner>.Instance).CleanRestoreJournalTemporaries(temps);

        Assert.All(temps, temp => Assert.False(File.Exists(temp)));
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(7, temps.Count);
    }

    // The composition root: one gate everywhere, the raw store only behind UngatedCredentialStore, the backup
    // service resolvable, and NO entitlement dependency on the path (C-12).
    [Fact]
    public async Task CompositionRoot_WiresTheGateAndTheBackupService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        App.ConfigureApplicationServices(services);
        await using var provider = services.BuildServiceProvider();

        var gate = provider.GetRequiredService<IConfigurationWriteGate>();
        Assert.Same(gate, provider.GetRequiredService<ConfigurationWriteGate>());
        Assert.IsType<GatedCredentialStore>(provider.GetRequiredService<IServerCredentialStore>());
        Assert.IsType<WindowsCredentialStore>(provider.GetRequiredService<UngatedCredentialStore>().Store);
        Assert.IsType<ConfigurationBackupService>(provider.GetRequiredService<IConfigurationBackupService>());
        Assert.Same(
            provider.GetRequiredService<JsonNotificationSettingsService>(),
            provider.GetRequiredService<INotificationSettingsService>());

        var constructor = Assert.Single(typeof(ConfigurationBackupService).GetConstructors());
        Assert.DoesNotContain(
            constructor.GetParameters(),
            parameter => parameter.ParameterType.FullName!.Contains("Entitlement", StringComparison.OrdinalIgnoreCase));
    }

    private (JsonNotificationSettingsService, JsonBackgroundMonitoringSettingsService) CreateServices() =>
        (new JsonNotificationSettingsService(
                new NotificationSettingsStorageOptions { FilePath = NotificationPath },
                NullLogger<JsonNotificationSettingsService>.Instance,
                _gate),
            new JsonBackgroundMonitoringSettingsService(
                new BackgroundSettingsStorageOptions { FilePath = BackgroundPath },
                NullLogger<JsonBackgroundMonitoringSettingsService>.Instance,
                _gate));
}
