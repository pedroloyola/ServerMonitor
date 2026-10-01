using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.App.Windowing;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Security;
using ServerMonitor.Infrastructure.SshConfig;

namespace ServerMonitor.App.Tests.TestSupport;

/// <summary>
/// TEST-REALDATA-AUDIT (UI.3 gate 1C). The REAL composition root, with every per-user data root re-pointed at a
/// per-instance temp directory BEFORE anything is resolved: every storage options type, the ~/.ssh profile of the
/// import source, key discovery and the key picker, and the Credential Manager. Tests that build a provider from
/// <see cref="App.ConfigureApplicationServices"/> go through here; a test that only inspects descriptors may use
/// <see cref="ProductionDescriptors"/>, which is never built.
/// <para>
/// The Credential Manager cannot be redirected, so it is REPLACED: the raw store behind
/// <see cref="UngatedCredentialStore"/> is an in-memory one, and <see cref="WindowsCredentialStore"/> itself resolves
/// to a tripwire that throws. The production factories for the gated store and the backup engine still run.
/// </para>
/// <para>
/// <see cref="BuildProvider"/> runs the structural phase of <see cref="RealDataIsolationGuard"/> first, so a provider
/// with a real root in it is refused before any store is constructed.
/// </para>
/// </summary>
internal sealed class IsolatedAppComposition : IDisposable
{
    public IsolatedAppComposition()
    {
        Root = Path.Combine(Path.GetTempPath(), "sm-app-isolated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Services.AddLogging();
        App.ConfigureApplicationServices(Services);
        Isolate(Services, DataDirectory, UserProfile, Credentials);
    }

    /// <summary>The temp root every per-user path must stay under.</summary>
    public string Root { get; }

    /// <summary>Stands in for <c>%LOCALAPPDATA%\ServerMonitor</c>.</summary>
    public string DataDirectory => Path.Combine(Root, "AppData-Local", "ServerMonitor");

    /// <summary>Stands in for <c>%USERPROFILE%</c> (its <c>.ssh</c> is never created).</summary>
    public string UserProfile => Path.Combine(Root, "Profile");

    /// <summary>
    /// Every root a test may resolve into: <see cref="Root"/>, plus the assembly-wide QA root that
    /// <c>QaTestRoots</c> installs before any test runs (the --qa-* harnesses put their window placement there). The
    /// harnesses and their tests are Debug-only, so a Release run allows <see cref="Root"/> alone.
    /// </summary>
#if DEBUG
    public IReadOnlyList<string> TestRoots => [Root, Qa.QaTestRoots.Root];
#else
    public IReadOnlyList<string> TestRoots => [Root];
#endif

    public ServiceCollection Services { get; } = new();

    public InMemoryCredentialStore Credentials { get; } = new();

    /// <summary>The production descriptors, for shape assertions only. Never build a provider from this.</summary>
    public static ServiceCollection ProductionDescriptors()
    {
        var services = new ServiceCollection();
        App.ConfigureApplicationServices(services);
        return services;
    }

    public ServiceProvider BuildProvider()
    {
        var provider = Services.BuildServiceProvider();
        try
        {
            RealDataIsolationGuard.AssertStructurallyIsolated(Services, provider, TestRoots);
            return provider;
        }
        catch
        {
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Appends the overrides; last registration wins for every single-service resolution.</summary>
    internal static void Isolate(IServiceCollection services, string dataDirectory, string userProfile, IServerCredentialStore credentials)
    {
        var trust = new HostKeyTrustStorageOptions { FilePath = Path.Combine(dataDirectory, "known-hosts.json") };
        services.AddSingleton(new ServerStorageOptions { FilePath = Path.Combine(dataDirectory, "servers.json") });
        services.AddSingleton(trust);
        services.AddSingleton(RoutedHostKeyTrustStorageOptions.From(trust));
        services.AddSingleton(new NotificationSettingsStorageOptions { FilePath = Path.Combine(dataDirectory, "notification-settings.json") });
        services.AddSingleton(new BackgroundSettingsStorageOptions { FilePath = Path.Combine(dataDirectory, "background-settings.json") });
        services.AddSingleton(new WindowPlacementStorageOptions { FilePath = Path.Combine(dataDirectory, "window-placement.json") });
        services.AddSingleton(new HistoryStorageOptions { DatabasePath = Path.Combine(dataDirectory, "history.db") });
        services.AddSingleton(new WidgetStateOptions { SnapshotPath = Path.Combine(dataDirectory, "widget-state.json") });
        services.AddSingleton(new IgnoredDeviceStorageOptions { FilePath = Path.Combine(dataDirectory, "ignored-devices.json") });

        services.AddSingleton<ISshConfigImportSource>(_ => new SshConfigFileImportSource(userProfile));
        services.AddSingleton<ILocalSshKeyDiscovery>(_ => new LocalSshKeyDiscovery(userProfile));
        services.AddSingleton<IPrivateKeyFilePicker>(sp => new PrivateKeyFilePicker(sp.GetRequiredService<IWindowContext>(), userProfile));

        services.AddSingleton<WindowsCredentialStore>(_ => throw new RealDataTripwireException(
            "The real WindowsCredentialStore (Credential Manager) was resolved in a test."));
        services.AddSingleton(new UngatedCredentialStore(credentials));
    }

    /// <summary>A process-local credential store: nothing reaches the Credential Manager.</summary>
    internal sealed class InMemoryCredentialStore : IServerCredentialStore
    {
        private readonly ConcurrentDictionary<CredentialReference, string> _secrets = new();

        public Task WriteAsync(CredentialReference reference, SecretValue secret, CancellationToken cancellationToken = default)
        {
            _secrets[reference] = secret.RevealAsString();
            return Task.CompletedTask;
        }

        public Task<SecretValue?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.TryGetValue(reference, out var value) ? new SecretValue(value) : null);

        public Task<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.TryRemove(reference, out _));
    }
}

/// <summary>Thrown when a test reaches something that must only exist in production.</summary>
internal sealed class RealDataTripwireException(string message) : InvalidOperationException(message);
