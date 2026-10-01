using System.Text.RegularExpressions;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.2 incident guard (after a counterproof deleted a real %LOCALAPPDATA%\ServerMonitor file). WHAT IT PROVES, and
/// no more: it is a LEXICAL scan of tests/**. (1) DIRECT construction of the real application data path -
/// <c>…ForCurrentUser(</c> (incl. <c>DirectoryForCurrentUser(</c>), <c>SpecialFolder.LocalApplicationData</c>, any
/// <c>%LOCALAPPDATA%</c>/<c>LOCALAPPDATA</c> literal in any case, or <c>RealDataDirectory</c> - is allowed only where an
/// exact, justified entry says so (comparison-only, no file I/O). (2) INDIRECT reach through
/// <c>App.ConfigureApplicationServices(</c>, which registers the production <c>*StorageOptions.ForCurrentUser()</c>, is
/// counted against its own exact allowlist. Both lists fail on a new use AND on a stale entry, so they stay exact. New
/// tests use temp sentinels instead.
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
        ["tests/ServerMonitor.App.Tests/Qa/QaWindowPlacementIsolationTests.cs"] = 2
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
    public void RealDataPatternsAreCaught(string code) => Assert.Matches(RealDataPath(), code);

    private static Dictionary<string, int> Count(Regex pattern)
    {
        var repository = Path.GetFullPath(Path.Combine(AppSourceTree.AppRoot, "..", ".."));
        return Directory.EnumerateFiles(Path.Combine(repository, "tests"), "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(repository, path).Replace('\\', '/'))
            .Where(path => !path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) && !path.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            // This file names the patterns it looks for; it never builds the path.
            .Where(path => !path.EndsWith("/TestRealDataPathGuardTests.cs", StringComparison.Ordinal))
            .Select(path => (path, count: pattern.Matches(StripComments(File.ReadAllText(Path.Combine(repository, path)))).Count))
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

    private static string StripComments(string code) => Comment().Replace(code, " ");

    [GeneratedRegex(@"ForCurrentUser\s*\(|SpecialFolder\.LocalApplicationData|%?LOCALAPPDATA%?|\bRealDataDirectory\b", RegexOptions.IgnoreCase)]
    private static partial Regex RealDataPath();

    [GeneratedRegex(@"\bConfigureApplicationServices\s*\(")]
    private static partial Regex ProductionComposition();

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();
}
