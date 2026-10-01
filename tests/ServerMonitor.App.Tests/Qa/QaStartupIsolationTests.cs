using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    /// <summary>Every path the production-like composition carries, as the guard DISCOVERS it.</summary>
    private static readonly string[] ProductionPathNames =
    [
        "ServerStorageOptions.FilePath",
        "ServerStorageOptions.RoutedFilePath",
        "HostKeyTrustStorageOptions.FilePath",
        "RoutedHostKeyTrustStorageOptions.FilePath",
        "BackgroundSettingsStorageOptions.FilePath",
        "NotificationSettingsStorageOptions.FilePath",
        "WindowPlacementStorageOptions.FilePath",
        "SshConfigFileImportSource.ConfigPath",
        "LocalSshKeyDiscovery.SshDirectory",
        "PrivateKeyFilePicker.UserProfile"
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
        Assert.IsNotType<WindowsCredentialStore>(provider.GetRequiredService<UngatedCredentialStore>().Store);
    }

    /// <summary>Vigil L-1A: a path-bearing options type nobody listed is still found and judged.</summary>
    [Fact]
    public void AnUnlistedOptionsTypeWithAPath_IsDiscovered_AndRefusedWhenItPointsAtRealData()
    {
        var services = ProductionLike();
        QaStartupIsolation.Apply(services, IsolationRoot, rerootSshProfile: true);
        services.AddSingleton(new ProbeCacheOptions { CacheDirectory = Path.Combine(Sentinel, "cache") });
        using var provider = services.BuildServiceProvider();

        Assert.Contains(QaStartupIsolation.ResolvedPaths(provider), pair => pair.Name == "ProbeCacheOptions.CacheDirectory");
        var refusal = Assert.Throws<InvalidOperationException>(
            () => QaStartupIsolation.VerifyOrThrow(provider, AllowedRoots, ForbiddenRoots));
        Assert.Contains("ProbeCacheOptions.CacheDirectory = ", refusal.Message);
    }

    [Fact]
    public void ACompositionMissingARequiredOption_IsRefused()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWindowContext, WindowContext>();
        QaStartupIsolation.Apply(services, IsolationRoot, rerootSshProfile: true);
        services.RemoveAll<NotificationSettingsStorageOptions>();
        using var provider = services.BuildServiceProvider();

        var refusal = Assert.Throws<InvalidOperationException>(
            () => QaStartupIsolation.VerifyOrThrow(provider, AllowedRoots, ForbiddenRoots));
        Assert.Contains("NotificationSettingsStorageOptions (not registered)", refusal.Message);
    }

    /// <summary>Any credential store is accepted except the native Credential Manager one, wherever it is exposed.</summary>
    [Fact]
    public void TheCredentialManager_IsRefusedAlsoWhenRegisteredDirectly_AnyOtherStoreIsAccepted()
    {
        var services = ProductionLike();
        QaStartupIsolation.Apply(services, IsolationRoot, rerootSshProfile: true);
        services.AddSingleton<IServerCredentialStore>(UninitializedCredentialManager());
        using (var provider = services.BuildServiceProvider())
        {
            var refusal = Assert.Throws<InvalidOperationException>(
                () => QaStartupIsolation.VerifyOrThrow(provider, AllowedRoots, ForbiddenRoots));
            Assert.Contains("IServerCredentialStore = Windows Credential Manager", refusal.Message);
        }

        services.AddSingleton<IServerCredentialStore>(new QaInMemoryCredentialStore());
        using (var provider = services.BuildServiceProvider())
        {
            QaStartupIsolation.VerifyOrThrow(provider, AllowedRoots, ForbiddenRoots);
        }
    }

    // ---- wiring (Vigil M-1A-2): the guard and the re-root only count if the App really calls them ----

    [Fact]
    public void VerifyOrThrow_RunsInTheAppConstructor_AfterTheHostIsBuilt_AndBeforeItsResourcesLoad()
    {
        var source = AppSourceWithoutComments();
        var refusal = Single(source, "Qa.QaStartupIsolation.RefuseUnisolatedLaunch();");
        var build = Single(source, ".Build();");
        var verify = Single(source, "Qa.QaStartupIsolation.VerifyOrThrow(ServicesHost.Services);");
        var initialize = source.IndexOf("InitializeComponent();", verify, StringComparison.Ordinal);
        var start = Single(source, "await ServicesHost.StartAsync();");

        Assert.True(refusal < build && build < verify, "VerifyOrThrow must follow the host build (and the refusal)");
        Assert.True(initialize > verify && initialize < start, "VerifyOrThrow must precede the App's InitializeComponent and the host start");
    }

    [Fact]
    public void Apply_IsRegisteredForEveryHarness_BeforeTheHarnessCompositions()
    {
        var source = AppSourceWithoutComments();
        var qaMode = Single(source, "var qaMode = Qa.QaStartupIsolation.IsHarnessLaunch();");
        var guarded = source.IndexOf("if (qaMode)", qaMode, StringComparison.Ordinal);
        var apply = Single(source, "Qa.QaStartupIsolation.Apply(");
        var production = source.IndexOf("if (!qaMode)", apply, StringComparison.Ordinal);
        var firstHarness = Single(source, "Qa.QaHealthComposition.Apply(services);");

        Assert.True(qaMode < guarded && guarded < apply, "Apply must sit inside 'if (qaMode)' right after qaMode is decided");
        Assert.True(production > apply && apply < firstHarness, "Apply must be registered before every harness composition");
        Assert.Contains("Qa.QaStartupIsolation.DefaultRoot()", source[apply..production]);
    }

    private static string AppSourceWithoutComments() =>
        // A commented-out call is not a call.
        System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Architecture.AppSourceTree.Full("App.xaml.cs")), @"//[^\r\n]*", string.Empty);

    private static int Single(string source, string anchor)
    {
        var first = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(first >= 0, $"'{anchor}' not found in App.xaml.cs");
        Assert.True(source.IndexOf(anchor, first + 1, StringComparison.Ordinal) < 0, $"'{anchor}' appears more than once");
        return first;
    }

    private static WindowsCredentialStore UninitializedCredentialManager() =>
        (WindowsCredentialStore)RuntimeHelpers.GetUninitializedObject(typeof(WindowsCredentialStore));

    /// <summary>Stands in for a future path-bearing options type the guard was never told about.</summary>
    private sealed class ProbeCacheOptions
    {
        public required string CacheDirectory { get; init; }
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
        services.AddSingleton(new UngatedCredentialStore(UninitializedCredentialManager()));
        services.AddSingleton<ISshConfigImportSource>(new SshConfigFileImportSource(Sentinel));
        services.AddSingleton<ILocalSshKeyDiscovery>(new LocalSshKeyDiscovery(Sentinel));
        services.AddSingleton<IWindowContext, WindowContext>();
        // The production registration: the container picks the public constructor, which means "the real profile".
        services.AddSingleton<IPrivateKeyFilePicker, PrivateKeyFilePicker>();
        return services;
    }
}
