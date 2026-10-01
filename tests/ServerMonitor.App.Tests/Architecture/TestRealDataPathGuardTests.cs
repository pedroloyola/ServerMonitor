using System.Text.RegularExpressions;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.2 incident guard (after a counterproof deleted a real %LOCALAPPDATA%\ServerMonitor file). WHAT IT PROVES, and
/// no more: it is a LEXICAL scan of tests/**. (1) DIRECT construction of the real application data path -
/// <c>…ForCurrentUser(</c> (incl. <c>DirectoryForCurrentUser(</c>), <c>SpecialFolder.LocalApplicationData</c>, any
/// <c>%LOCALAPPDATA%</c>/<c>LOCALAPPDATA</c> literal in any case, or <c>RealDataDirectory</c> - is allowed only where an
/// exact, justified entry says so (comparison-only, no file I/O). (2) INDIRECT reach through
/// <c>App.ConfigureApplicationServices(</c>, which registers the production <c>*StorageOptions.ForCurrentUser()</c>, is
/// counted against its own exact allowlist; those pre-UI.2 tests are NOT audited here (backlog TEST-REALDATA-AUDIT).
/// Both lists fail on a new use AND on a stale entry, so they stay exact. New tests use temp sentinels instead.
/// </summary>
public sealed partial class TestRealDataPathGuardTests
{
    /// <summary>Direct real-data-path constructions per file (repo-relative), each comparison-only.</summary>
    private static readonly IReadOnlyDictionary<string, int> DirectAllowlist = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // Computes the real root only to assert the harness backup folder is NOT under it. No I/O.
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
    /// Indirect reach: calls of App.ConfigureApplicationServices( per file. Pre-UI.2 composition tests; whether any of them
    /// performs I/O on a production path is NOT audited by this guard - backlog TEST-REALDATA-AUDIT (Boss). The list only
    /// stops the number of such tests from growing silently.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> IndirectAllowlist = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["tests/ServerMonitor.App.Tests/Architecture/CommercialCompositionSeamTests.cs"] = 1,
        ["tests/ServerMonitor.App.Tests/Architecture/FeatureCompositionRootTests.cs"] = 1,
        ["tests/ServerMonitor.App.Tests/Architecture/TrayOwnershipCompletenessTests.cs"] = 1,
        ["tests/ServerMonitor.App.Tests/Qa/QaBackupCompositionTests.cs"] = 1,
        ["tests/ServerMonitor.App.Tests/Qa/QaBackupHarnessTests.cs"] = 2,
        ["tests/ServerMonitor.App.Tests/Qa/QaProxyJumpHarnessTests.cs"] = 1,
        ["tests/ServerMonitor.App.Tests/Qa/QaSshConfigHarnessTests.cs"] = 3,
        ["tests/ServerMonitor.App.Tests/Services/BackupSettingsParticipantTests.cs"] = 1,
        ["tests/ServerMonitor.App.Tests/Services/StartupRestoreRecoveryTests.cs"] = 1
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
