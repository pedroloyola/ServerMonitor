using System.Text.RegularExpressions;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.2 incident guard (Boss, after a counterproof deleted a real %LOCALAPPDATA%\ServerMonitor file): a test or fixture
/// in tests/** may construct the REAL application data path only where an explicit, justified entry below says so -
/// and every such use is comparison-only (no file I/O on it). A real path handed to code under test turns any
/// regression - or any mutation during a counterproof - into real data loss, so new tests use temp sentinels instead.
/// Fails on a new use (count above the entry) AND on a stale entry (count below it), so the list stays exact.
/// </summary>
public sealed partial class TestRealDataPathGuardTests
{
    /// <summary>File (repo-relative) -> number of real-data-path constructions, each justified.</summary>
    private static readonly IReadOnlyDictionary<string, int> Allowlist = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // Computes the real root only to assert the harness backup folder is NOT under it. No I/O.
        ["tests/ServerMonitor.App.Tests/Qa/QaBackupCompositionTests.cs"] = 1,
        // Proves QaGalleryComposition.RealDataDirectory equals the app's real folder and that the PURE policy refuses it. No I/O.
        ["tests/ServerMonitor.App.Tests/Qa/QaGalleryHarnessTests.cs"] = 1,
        // Computes the real root only to assert the proxy-jump harness paths are NOT under it. No I/O.
        ["tests/ServerMonitor.App.Tests/Qa/QaProxyJumpHarnessTests.cs"] = 1,
        // (1) real root for "never under real data" comparisons; (2) the production options registration reproduced
        // to prove the harness overrides it - the store is resolved, never loaded or saved. No I/O.
        ["tests/ServerMonitor.App.Tests/Qa/QaWindowPlacementIsolationTests.cs"] = 2
    };

    [Fact]
    public void TestsConstructTheRealAppDataPathOnlyWhereJustified()
    {
        var repository = Path.GetFullPath(Path.Combine(AppSourceTree.AppRoot, "..", ".."));
        var tests = Path.Combine(repository, "tests");
        var counts = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(repository, path).Replace('\\', '/'))
            .Where(path => !path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) && !path.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            // This file names the patterns it looks for; it never builds the path.
            .Where(path => !path.EndsWith("/TestRealDataPathGuardTests.cs", StringComparison.Ordinal))
            .ToDictionary(path => path, path => RealDataPath().Matches(StripComments(File.ReadAllText(Path.Combine(repository, path)))).Count, StringComparer.Ordinal)
            .Where(pair => pair.Value > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        var failures = counts.Where(pair => pair.Value > Allowlist.GetValueOrDefault(pair.Key))
            .Select(pair => $"{pair.Key}: {pair.Value} real-data-path construction(s), allowed {Allowlist.GetValueOrDefault(pair.Key)} - use a temp sentinel")
            .Concat(Allowlist.Where(pair => counts.GetValueOrDefault(pair.Key) < pair.Value)
                .Select(pair => $"{pair.Key}: allowlist says {pair.Value}, found {counts.GetValueOrDefault(pair.Key)} - lower or remove the entry"))
            .ToList();

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string StripComments(string code) => Comment().Replace(code, " ");

    /// <summary>The ways a test reaches the real data folder: a ForCurrentUser() storage factory, the LocalApplicationData
    /// special folder, or the LOCALAPPDATA variable.</summary>
    [GeneratedRegex(@"\bForCurrentUser\s*\(|SpecialFolder\.LocalApplicationData|""LOCALAPPDATA""")]
    private static partial Regex RealDataPath();

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();
}
