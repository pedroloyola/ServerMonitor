using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Qa;
using ServerMonitor.App.Services;
using ServerMonitor.App.Windowing;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Security;
using ServerMonitor.Infrastructure.SshConfig;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// UI.3 gate 1A: the Debug harness isolation. The production root is reproduced over a temporary SENTINEL folder that
/// stands in for the real data folder (these tests never compute or touch the real one): every path the production
/// root registers points into the sentinel, the credential store is the Credential Manager type and the SSH sources use
/// the sentinel profile. <see cref="QaStartupIsolation.VerifyOrThrow(IServiceProvider, IReadOnlyList{string}, IReadOnlyList{string})"/>
/// must refuse that, and accept it only after <see cref="QaStartupIsolation.Apply"/> re-rooted every one of them -
/// so removing any single re-root fails <see cref="Apply_ReRootsEveryProductionPath_AndTheGuardAccepts"/>.
/// </summary>
public sealed class QaStartupIsolationTests
{
    private static readonly string Sentinel = Path.Combine(
        Path.GetTempPath(), "ServerMonitor-QA-sentinel", $"{Environment.ProcessId}-{Guid.NewGuid():N}");

    private static readonly string IsolationRoot = Path.Combine(QaTestRoots.Root, "isolated", "test");

    private static readonly string[] AllowedRoots = [QaTestRoots.Root];

    private static readonly string[] ForbiddenRoots = [Sentinel];

    /// <summary>Every path-bearing registration the production root makes; a new one must be added here and to Apply.</summary>
    private static readonly string[] ProductionPathNames =
    [
        nameof(ServerStorageOptions),
        nameof(HostKeyTrustStorageOptions),
        nameof(RoutedHostKeyTrustStorageOptions),
        nameof(BackgroundSettingsStorageOptions),
        nameof(NotificationSettingsStorageOptions),
        nameof(WindowPlacementStorageOptions),
        nameof(SshConfigFileImportSource),
        nameof(LocalSshKeyDiscovery),
        nameof(PrivateKeyFilePicker)
    ];

    [Fact]
    public void HarnessLaunchIsNotDetectedInTheTestHost()
    {
        Assert.False(QaStartupIsolation.IsHarnessLaunch());
        // Without a harness flag the launch-time check is a no-op, whatever the composition holds.
        using var provider = ProductionLike().BuildServiceProvider();
        QaStartupIsolation.VerifyOrThrow(provider);
    }

    [Fact]
    public void TheProductionCompositionAlone_IsRefused_NamingEveryPathAndTheCredentialManager()
    {
        using var provider = ProductionLike().BuildServiceProvider();

        var refusal = Assert.Throws<InvalidOperationException>(
            () => QaStartupIsolation.VerifyOrThrow(provider, AllowedRoots, ForbiddenRoots));

        Assert.All(ProductionPathNames, name => Assert.Contains($"{name} = ", refusal.Message));
        Assert.Contains("Windows Credential Manager", refusal.Message);
    }

    [Fact]
    public void Apply_ReRootsEveryProductionPath_AndTheGuardAccepts()
    {
        var services = ProductionLike();
        QaStartupIsolation.Apply(services, IsolationRoot, rerootSshProfile: true);
        using var provider = services.BuildServiceProvider();

        QaStartupIsolation.VerifyOrThrow(provider, AllowedRoots, ForbiddenRoots);

        var resolved = QaStartupIsolation.ResolvedPaths(provider).ToList();
        Assert.Equal(ProductionPathNames.OrderBy(n => n), resolved.Select(p => p.Name).OrderBy(n => n));
        Assert.All(resolved, pair => Assert.True(
            QaStartupIsolation.IsUnder(Path.GetFullPath(pair.Path!), IsolationRoot), $"{pair.Name} = {pair.Path}"));
        Assert.IsType<QaInMemoryCredentialStore>(provider.GetRequiredService<UngatedCredentialStore>().Store);
    }

    [Fact]
    public void AHarnessWithItsOwnDirectory_StillWins_AndIsAcceptedWhenThatDirectoryIsAllowed()
    {
        var own = Path.Combine(QaTestRoots.Root, "proxyjump-own");
        var services = ProductionLike();
        QaStartupIsolation.Apply(services, IsolationRoot, rerootSshProfile: true);
        QaProxyJumpComposition.Apply(services, own);
        using var provider = services.BuildServiceProvider();

        QaStartupIsolation.VerifyOrThrow(provider, AllowedRoots, ForbiddenRoots);
        Assert.StartsWith(own, provider.GetRequiredService<ServerStorageOptions>().FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(own, provider.GetRequiredService<HostKeyTrustStorageOptions>().FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnSshConfigFixture_IsKeptWhenTheSshProfileIsNotReRooted()
    {
        var fixture = Path.Combine(QaTestRoots.Root, "ssh-fixture");
        var services = ProductionLike();
        QaSshConfigComposition.Apply(services, fixture);
        QaStartupIsolation.Apply(services, IsolationRoot, rerootSshProfile: false);
        using var provider = services.BuildServiceProvider();

        QaStartupIsolation.VerifyOrThrow(provider, AllowedRoots, ForbiddenRoots);
        var keys = Assert.IsType<LocalSshKeyDiscovery>(provider.GetRequiredService<ILocalSshKeyDiscovery>());
        Assert.Equal(Path.Combine(fixture, ".ssh"), keys.SshDirectory);
    }

    [Fact]
    public void AnAllowedRootInsideARealDataFolder_IsStillRefused()
    {
        var services = ProductionLike();
        QaStartupIsolation.Apply(services, Path.Combine(Sentinel, "qa"), rerootSshProfile: true);
        using var provider = services.BuildServiceProvider();

        var refusal = Assert.Throws<InvalidOperationException>(
            () => QaStartupIsolation.VerifyOrThrow(provider, [Sentinel], ForbiddenRoots));
        Assert.Contains("under a real data folder", refusal.Message);
    }

    [Theory]
    [InlineData(null, "not an absolute path")]
    [InlineData(@"relative\servers.json", "not an absolute path")]
    public void Violation_RefusesNonAbsolutePaths(string? path, string reason) =>
        Assert.Equal(reason, QaStartupIsolation.Violation(path, AllowedRoots, ForbiddenRoots));

    [Fact]
    public void Violation_RefusesAPrefixSiblingOfTheAllowedRoot()
    {
        // "<root>-evil" shares the string prefix of "<root>" but is not under it.
        var sibling = QaTestRoots.Root + "-evil" + Path.DirectorySeparatorChar + "servers.json";
        Assert.Equal("outside the QA roots", QaStartupIsolation.Violation(sibling, AllowedRoots, ForbiddenRoots));
    }

    [Fact]
    public void DefaultRoot_IsPerProcessUnderTheQaRoot()
    {
        var root = QaStartupIsolation.DefaultRoot();
        Assert.True(QaStartupIsolation.IsUnder(root, QaTestRoots.Root));
        Assert.EndsWith(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), root);
    }

    [Fact]
    public void Apply_RejectsARelativeRoot() =>
        Assert.Throws<ArgumentException>(() => QaStartupIsolation.Apply(new ServiceCollection(), @"relative\dir", true));

    /// <summary>
    /// The production root's path-bearing registrations, pointed at the sentinel. The Credential Manager store is an
    /// uninitialized instance: its type is what the guard checks, and no constructor (so no native handle) ever runs.
    /// </summary>
    private static ServiceCollection ProductionLike()
    {
        var services = new ServiceCollection();
        var trust = new HostKeyTrustStorageOptions { FilePath = Path.Combine(Sentinel, "known-hosts.json") };
        services.AddSingleton(new ServerStorageOptions { FilePath = Path.Combine(Sentinel, "servers.json") });
        services.AddSingleton(trust);
        services.AddSingleton(sp => RoutedHostKeyTrustStorageOptions.From(sp.GetRequiredService<HostKeyTrustStorageOptions>()));
        services.AddSingleton(new BackgroundSettingsStorageOptions { FilePath = Path.Combine(Sentinel, "background-settings.json") });
        services.AddSingleton(new NotificationSettingsStorageOptions { FilePath = Path.Combine(Sentinel, "notification-settings.json") });
        services.AddSingleton(new WindowPlacementStorageOptions { FilePath = Path.Combine(Sentinel, "window-placement.json") });
        services.AddSingleton(new UngatedCredentialStore(
            (WindowsCredentialStore)RuntimeHelpers.GetUninitializedObject(typeof(WindowsCredentialStore))));
        services.AddSingleton<ISshConfigImportSource>(new SshConfigFileImportSource(Sentinel));
        services.AddSingleton<ILocalSshKeyDiscovery>(new LocalSshKeyDiscovery(Sentinel));
        services.AddSingleton<IWindowContext, WindowContext>();
        // The production registration: the container picks the public constructor, which means "the real profile".
        services.AddSingleton<IPrivateKeyFilePicker, PrivateKeyFilePicker>();
        return services;
    }
}
