using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Backup;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.App.Tests.Services;

// Cortex-2: startup recovery acts on real data only from a launch that owns the single-instance key. A Debug
// --qa-* launch bypasses the key (it may run next to a normal instance that is mid-Apply) and must never recover.
// The "real" profile here is a temp directory wired through the REAL composition root's options.
public sealed class StartupRestoreRecoveryTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "sm-startup-recovery-" + Guid.NewGuid().ToString("N"));

    public StartupRestoreRecoveryTests()
    {
        Directory.CreateDirectory(_profile);
    }

    private string JournalDirectory => Path.Combine(_profile, "restore-journal");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_profile, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("--qa-health")]
    [InlineData("--qa-proxyjump")]
    [InlineData("--qa-backup")]
    public async Task AQaLaunch_NeverRecoversTheRealProfilesJournal(string qaArgument)
    {
        // An orphan journal (no manifest): a recovery that runs deletes it — the observable destructive action.
        Directory.CreateDirectory(Path.Combine(JournalDirectory, "pre"));
        await using var provider = Build();

        var report = await StartupRestoreRecovery.RunAsync(provider, ["ServerMonitor.App.exe", qaArgument], isDebugBuild: true);

        Assert.True(Directory.Exists(Path.Combine(JournalDirectory, "pre")));
        Assert.Equal(RestoreRecoveryOutcome.NothingToRecover, report.Outcome);
        Assert.Same(report, provider.GetRequiredService<RestoreRecoveryStatus>().Report);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AKeyOwningLaunch_Recovers(bool isDebugBuild)
    {
        Directory.CreateDirectory(Path.Combine(JournalDirectory, "pre"));
        await using var provider = Build();

        var report = await StartupRestoreRecovery.RunAsync(provider, ["ServerMonitor.App.exe"], isDebugBuild);

        Assert.False(Directory.Exists(JournalDirectory));
        Assert.Equal(JournalDirectory, report.JournalDirectory);
    }

    [Fact]
    public async Task ReleaseBuild_RecoversEvenWithAQaArgument_BecauseItAlwaysOwnsTheKey()
    {
        Directory.CreateDirectory(Path.Combine(JournalDirectory, "pre"));
        await using var provider = Build();

        await StartupRestoreRecovery.RunAsync(provider, ["ServerMonitor.App.exe", "--qa-health"], isDebugBuild: false);

        Assert.False(Directory.Exists(JournalDirectory));
    }

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        App.ConfigureApplicationServices(services);
        var trust = new HostKeyTrustStorageOptions { FilePath = Path.Combine(_profile, "known-hosts.json") };
        services.AddSingleton(new ServerStorageOptions { FilePath = Path.Combine(_profile, "servers.json") });
        services.AddSingleton(trust);
        services.AddSingleton(RoutedHostKeyTrustStorageOptions.From(trust));
        services.AddSingleton(new NotificationSettingsStorageOptions { FilePath = Path.Combine(_profile, "notification-settings.json") });
        services.AddSingleton(new BackgroundSettingsStorageOptions { FilePath = Path.Combine(_profile, "background-settings.json") });
        services.AddSingleton(new UngatedCredentialStore(new NoCredentials()));
        return services.BuildServiceProvider();
    }

    private sealed class NoCredentials : IServerCredentialStore
    {
        public Task WriteAsync(CredentialReference reference, SecretValue secret, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("not expected");

        public Task<SecretValue?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult<SecretValue?>(null);

        public Task<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
