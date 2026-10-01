using System.Text.RegularExpressions;
using ServerMonitor.TestSupport;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.2 incident guard (after a counterproof deleted a real %LOCALAPPDATA%\ServerMonitor file). WHAT IT PROVES, and
/// no more: it is a LEXICAL scan of tests/**. (1) DIRECT construction of the real application data path -
/// <c>…ForCurrentUser(</c> (incl. <c>DirectoryForCurrentUser(</c>), ANY <c>SpecialFolder.*</c>, any
/// <c>%LOCALAPPDATA%</c>/<c>LOCALAPPDATA</c> literal in any case, or <c>RealDataDirectory</c> - is allowed only where an
/// exact, justified entry says so (comparison-only, no file I/O). (2) INDIRECT reach through
/// <c>App.ConfigureApplicationServices(</c>, which registers the production <c>*StorageOptions.ForCurrentUser()</c>, is
/// counted against its own exact allowlist. (3) DEFAULT-ROOT constructors (Vigil M-1C-2): the types whose omitted or
/// <c>null</c> path means the REAL profile - <c>WidgetOrphanTempCleaner</c> (deletes), <c>WidgetSnapshotReader</c>,
/// <c>FileSystemSnapshotChangeSource</c>, <c>WidgetProviderCoordinator.CreateWithFileSystemPump</c> (widget-state),
/// <c>LocalSshKeyDiscovery</c>, <c>SshConfigFileImportSource</c>, <c>PrivateKeyFilePicker</c> (~/.ssh) - constructed with
/// no path, <c>null</c>/<c>default</c>, a named argument that skips the path, or target-typed <c>new()</c>; matched on
/// code with literals blanked. All lists fail on a new use AND on a stale entry, so they stay exact. New tests use temp
/// sentinels instead. Comments are stripped by a string-aware lexer (<see cref="CSharpSourceText"/>), so a <c>//</c>
/// inside a string (a URL) no longer hides the rest of the line.
/// <para>
/// TEST-REALDATA-AUDIT (UI.3 gate 1C): this lexical guard is now only the fence. The proof is
/// <see cref="RealDataIsolationGuardTests"/> - a structural guard over what an isolated provider REALLY holds - and
/// every test that BUILDS a provider from the root goes through <c>TestSupport/IsolatedAppComposition</c>.
/// </para>
/// </summary>
public sealed partial class TestRealDataPathGuardTests
{
    /// <summary>Direct real-data-path constructions per file (repo-relative), each comparison-only.</summary>
    private static readonly IReadOnlyDictionary<string, int> DirectAllowlist = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // Computes the real root only to assert the PRODUCTION descriptor (never built) targets it. No I/O.
        ["tests/ServerMonitor.App.Tests/Qa/QaBackupCompositionTests.cs"] = 1,
        // Proves QaGalleryComposition.RealDataDirectory equals the app's real folder and that the PURE policy refuses it
        // (and that the default report folder is not under it). No I/O.
        ["tests/ServerMonitor.App.Tests/Qa/QaGalleryHarnessTests.cs"] = 4,
        // Computes the real root only to assert the proxy-jump harness paths are NOT under it. No I/O.
        ["tests/ServerMonitor.App.Tests/Qa/QaProxyJumpHarnessTests.cs"] = 1,
        // (1) real root for "never under real data" comparisons; (2) the production options registration reproduced
        // to prove the harness overrides it - the store is resolved, never loaded or saved. No I/O.
        ["tests/ServerMonitor.App.Tests/Qa/QaWindowPlacementIsolationTests.cs"] = 2,
        // SpecialFolder.UserProfile x2: the expected ~/.ssh path string the DEFAULT sources must point at. Compared only;
        // no file under it is opened.
        ["tests/ServerMonitor.App.Tests/Qa/QaSshConfigHarnessTests.cs"] = 2,
        // SpecialFolder.System: locates the OS's powershell.exe (System32) to start a child process. Not user data.
        ["tests/ServerMonitor.Infrastructure.Tests/SSH/LoopbackOriginatorGateTests.cs"] = 1,
        // SpecialFolder.ProgramFiles: File.Exists on PowerShell 7's pwsh.exe to pick a shell. Not user data.
        ["tests/ServerMonitor.Infrastructure.Tests/SshConfig/SshConfigQaFixturesScriptTests.cs"] = 1
    };

    /// <summary>
    /// Indirect reach: calls of App.ConfigureApplicationServices( per file. Audited in TEST-REALDATA-AUDIT (UI.3 gate 1C,
    /// .boss/tmp/ui3/cortex/report.md); each remaining entry says what it resolves.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> IndirectAllowlist = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // THE entry point: ProductionDescriptors() (never built) and the isolated composition (temp roots + guard).
        ["tests/ServerMonitor.App.Tests/TestSupport/IsolatedAppComposition.cs"] = 2,
        // Harness layered on the production root; resolves only options (asserted inside the harness temp dir), the
        // in-memory credential store and IServerProfileService over harness paths. Constructed only, no real I/O.
        ["tests/ServerMonitor.App.Tests/Qa/QaProxyJumpHarnessTests.cs"] = 1,
        // Two of three build the production root on purpose to prove the DEFAULT sources point at the real profile:
        // SshConfigFileImportSource / LocalSshKeyDiscovery / PrivateKeyFilePicker are constructed (no I/O in their
        // constructors) and only their path strings compared; no file is opened.
        ["tests/ServerMonitor.App.Tests/Qa/QaSshConfigHarnessTests.cs"] = 3
    };

    /// <summary>Default-root constructions per file (repo-relative), each constructed only - no I/O.</summary>
    private static readonly IReadOnlyDictionary<string, int> DefaultRootAllowlist = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // new LocalSshKeyDiscovery() registered as the default the harness must OVERRIDE; the harness registration wins,
        // so this factory is never invoked and nothing is constructed on ~/.ssh.
        ["tests/ServerMonitor.App.Tests/Qa/QaSshConfigHarnessTests.cs"] = 1
    };

    [Fact]
    public void TestsConstructTheRealAppDataPathOnlyWhereJustified() =>
        AssertExact(Count(RealDataPath()), DirectAllowlist, "direct real-data-path construction(s)");

    [Fact]
    public void TestsReachTheProductionCompositionOnlyWhereListed() =>
        AssertExact(Count(ProductionComposition()), IndirectAllowlist, "App.ConfigureApplicationServices( call(s) (TEST-REALDATA-AUDIT)");

    /// <summary>The patterns catch the forms Vigil F-5 showed the first version missed.</summary>
    [Theory]
    [InlineData("WidgetStateLocation.DirectoryForCurrentUser()")]
    [InlineData("ServerStorageOptions.ForCurrentUser ()")]
    [InlineData("Environment.ExpandEnvironmentVariables(\"%LOCALAPPDATA%\\\\ServerMonitor\")")]
    [InlineData("Environment.GetEnvironmentVariable(\"LocalAppData\")")]
    [InlineData("Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)")]
    [InlineData("QaGalleryComposition.RealDataDirectory")]
    [InlineData("Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)")]
    [InlineData("Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)")]
    public void RealDataPatternsAreCaught(string code) => Assert.Matches(RealDataPath(), code);

    [Fact]
    public void TestsUseTheRealProfileDefaultOfAConstructorOnlyWhereJustified() =>
        AssertExact(Count(DefaultRealRoot(), CSharpSourceText.CodeOnly), DefaultRootAllowlist, "default-real-root construction(s) (Vigil M-1C-2)");

    [Theory]
    [InlineData("new WidgetOrphanTempCleaner()")]
    [InlineData("new WidgetOrphanTempCleaner(null, log)")]
    [InlineData("new WidgetOrphanTempCleaner(log: log)")]
    [InlineData("new WidgetSnapshotReader(\n    null,\n    maxBytes: 5)")]
    [InlineData("new WidgetSnapshotReader(timeProvider: clock)")]
    [InlineData("new FileSystemSnapshotChangeSource(default)")]
    [InlineData("new FileSystemSnapshotChangeSource()")]
    [InlineData("WidgetProviderCoordinator.CreateWithFileSystemPump(host)")]
    [InlineData("WidgetProviderCoordinator.CreateWithFileSystemPump(\n    host,\n    debounce: d)")]
    [InlineData("WidgetProviderCoordinator.CreateWithFileSystemPump(host, null, clock)")]
    [InlineData("new LocalSshKeyDiscovery()")]
    [InlineData("new SshConfigFileImportSource( )")]
    [InlineData("LocalSshKeyDiscovery discovery = new();")]
    [InlineData("new PrivateKeyFilePicker(context)")]
    [InlineData("new PrivateKeyFilePicker(sp.GetRequiredService<IWindowContext>(), null)")]
    [InlineData("new PrivateKeyFilePicker(context, userProfile: null)")]
    public void DefaultRealRootPatternsAreCaught(string code) =>
        Assert.Single(DefaultRealRoot().Matches(CSharpSourceText.CodeOnly(code)));

    [Theory]
    [InlineData("new WidgetOrphanTempCleaner(_dir)")]
    [InlineData("new WidgetSnapshotReader(_dir, null, _clock)")]
    [InlineData("new WidgetSnapshotReader(path: p, maxBytes: 5)")]
    [InlineData("new FileSystemSnapshotChangeSource(_path)")]
    [InlineData("WidgetProviderCoordinator.CreateWithFileSystemPump(host, _path, debounce: d)")]
    [InlineData("WidgetProviderCoordinator.CreateWithFileSystemPump(host, snapshotPath: p)")]
    [InlineData("new LocalSshKeyDiscovery(Profile, fs.Describe)")]
    [InlineData("new SshConfigFileImportSource(_profile, RealOpen)")]
    [InlineData("new PrivateKeyFilePicker(sp.GetRequiredService<IWindowContext>(), userProfile)")]
    [InlineData("var s = \"new LocalSshKeyDiscovery()\"; // new LocalSshKeyDiscovery()")]
    public void AnExplicitPathOrTextIsNotCaught(string code) =>
        Assert.Empty(DefaultRealRoot().Matches(CSharpSourceText.CodeOnly(code)));

    private static Dictionary<string, int> Count(Regex pattern) => Count(pattern, CSharpSourceText.StripComments);

    private static Dictionary<string, int> Count(Regex pattern, Func<string, string> prepare)
    {
        var repository = Path.GetFullPath(Path.Combine(AppSourceTree.AppRoot, "..", ".."));
        return Directory.EnumerateFiles(Path.Combine(repository, "tests"), "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(repository, path).Replace('\\', '/'))
            .Where(path => !path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) && !path.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            // This file names the patterns it looks for; it never builds the path.
            .Where(path => !path.EndsWith("/TestRealDataPathGuardTests.cs", StringComparison.Ordinal))
            .Select(path => (path, count: pattern.Matches(prepare(File.ReadAllText(Path.Combine(repository, path)))).Count))
            .Where(pair => pair.count > 0)
            .ToDictionary(pair => pair.path, pair => pair.count, StringComparer.Ordinal);
    }

    private static void AssertExact(Dictionary<string, int> counts, IReadOnlyDictionary<string, int> allowlist, string what)
    {
        var failures = counts.Where(pair => pair.Value > allowlist.GetValueOrDefault(pair.Key))
            .Select(pair => $"{pair.Key}: {pair.Value} {what}, allowed {allowlist.GetValueOrDefault(pair.Key)} - use a temp sentinel / temp options")
            .Concat(allowlist.Where(pair => counts.GetValueOrDefault(pair.Key) < pair.Value)
                .Select(pair => $"{pair.Key}: allowlist says {pair.Value}, found {counts.GetValueOrDefault(pair.Key)} - lower or remove the entry"))
            .ToList();

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [GeneratedRegex(@"ForCurrentUser\s*\(|SpecialFolder\.\w+|%?LOCALAPPDATA%?|\bRealDataDirectory\b", RegexOptions.IgnoreCase)]
    private static partial Regex RealDataPath();

    // Matched on code with literals blanked. The first argument is the path for every type but the coordinator
    // factory (second); a named argument that is not the path parameter means the path was omitted.
    [GeneratedRegex(
        @"new\s+(?:WidgetOrphanTempCleaner|WidgetSnapshotReader|FileSystemSnapshotChangeSource|LocalSshKeyDiscovery|SshConfigFileImportSource)\s*\(\s*(?:\)|(?:null|default)\s*[,)]|(?!(?:path|directory|snapshotPath|userProfile)\s*:)\w+\s*:(?!:))"
        + @"|(?:WidgetOrphanTempCleaner|WidgetSnapshotReader|FileSystemSnapshotChangeSource|LocalSshKeyDiscovery|SshConfigFileImportSource|PrivateKeyFilePicker)\s+\w+\s*=\s*new\s*\(\s*\)"
        + @"|CreateWithFileSystemPump\s*\(\s*[^,()]+(?:\)|,\s*(?:null|default)\s*[,)]|,\s*(?!snapshotPath\s*:)\w+\s*:(?!:))"
        + @"|new\s+PrivateKeyFilePicker\s*\(\s*[^,()]*(?:\([^()]*\))?[^,()]*(?:\)|,\s*(?:userProfile\s*:\s*)?(?:null|default)\s*\))")]
    private static partial Regex DefaultRealRoot();

    [GeneratedRegex(@"\bConfigureApplicationServices\s*\(")]
    private static partial Regex ProductionComposition();
}
