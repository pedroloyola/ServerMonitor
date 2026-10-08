using System.Text;
using System.Text.RegularExpressions;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// ADVISORY LINT ONLY (UI.5 fix round 5, Boss §16 non-convergence gate) - NOT the proof. The proof is the runtime guard:
/// <c>TransientNoticeTimer.RejectSystemTimeProvider</c>, armed for every App test run by <c>SystemClockTestGuard</c>
/// (a module initializer), makes any notice / toast countdown on the system clock throw, however the clock was obtained.
/// Omitting the clock does not compile (fix round 4).
/// <para>
/// This lint flags the obvious spelling - <c>PresentationClock.System</c> / <c>TimeProvider.System</c> in the App test
/// project's code - with a small tokenizer (no Roslyn in the test project): comments, plain / verbatim / raw literals
/// and char literals are blanked; the HOLES of interpolated strings are code and stay (Atlas C4: <c>$"{clock =
/// PresentationClock.System}"</c> counts). Known, accepted limits (no further investment): <c>using static</c>, aliases,
/// reflection, raw interpolated literals and a string literal nested inside a hole - all caught at run time instead.
/// </para>
/// </summary>
public sealed class SystemClockGuardTests
{
    private static readonly Regex SystemClock = new(@"\b(PresentationClock|TimeProvider)\s*\.\s*System\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// The documented exceptions - neither builds a notice owner. Exact count per file, so a stale or a new use fails.
    /// </summary>
    private static readonly Dictionary<string, int> Allowed = new(StringComparer.Ordinal)
    {
        // A tray state-machine test (another workstream) that needs the real clock for its gate.
        ["Architecture/TrayOwnershipCompletenessTests.cs"] = 1,
        // The composition-order test: it must name the root's system clock to prove the harness clock still wins.
        ["Qa/QaPresentationClockOrderTests.cs"] = 3
    };

    [Fact]
    public void NoTest_PassesTheSystemClock()
    {
        var root = Path.Combine(AppSourceTree.RepositoryRoot, "tests", "ServerMonitor.App.Tests");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var code = CodeOnly(File.ReadAllText(file));
            var hits = SystemClock.Matches(code);
            var allowed = Allowed.GetValueOrDefault(relative);
            if (hits.Count != allowed)
            {
                offenders.Add($"{relative}: {hits.Count} use(s) of the system clock (allowed {allowed}): "
                    + string.Join(", ", hits.Select(hit => $"line {code[..hit.Index].Count(c => c == '\n') + 1} '{hit.Value}'")));
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// UI.9 SPEC test 9: the widget provider derives every age (snapshot and per-server freshness, debounce,
    /// backstop, drain) from its injected <see cref="TimeProvider"/>. A direct wall-clock or stopwatch read in
    /// its source tree would make freshness untestable and could drift from the injected clock.
    /// </summary>
    [Fact]
    public void WidgetProvider_NeverReadsTheWallClockOrAStopwatchDirectly()
    {
        var root = Path.Combine(AppSourceTree.RepositoryRoot, "src", "ServerMonitor.WidgetProvider");
        var scanned = 0;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal))
            {
                continue;
            }

            scanned++;
            var code = CodeOnly(File.ReadAllText(file));
            foreach (Match hit in ProviderWallClock.Matches(code))
            {
                offenders.Add($"{relative}: line {code[..hit.Index].Count(c => c == '\n') + 1} '{hit.Value}'");
            }
        }

        Assert.True(scanned > 20, $"scanned only {scanned} provider files"); // the walk must be real
        Assert.Empty(offenders);
    }

    private static readonly Regex ProviderWallClock = new(
        @"\b(DateTime|DateTimeOffset)\s*\.\s*(Utc)?Now\b|\bStopwatch\b|\bEnvironment\s*\.\s*TickCount(64)?\b",
        RegexOptions.CultureInvariant);

    [Theory]
    [InlineData("var now = DateTime.UtcNow;", 1)]
    [InlineData("var now = DateTimeOffset . Now;", 1)]
    [InlineData("var sw = System.Diagnostics.Stopwatch.StartNew();", 1)]
    [InlineData("var t = Environment.TickCount64;", 1)]
    [InlineData("var now = _timeProvider.GetUtcNow(); // not DateTime.UtcNow", 0)]
    [InlineData("var s = \"DateTime.Now\";", 0)]
    public void TheProviderGuard_CountsCodeUses_IgnoringCommentsAndStrings(string source, int expected) =>
        Assert.Equal(expected, ProviderWallClock.Matches(CodeOnly(source)).Count);

    // The guard's own counterproofs: it decides on code, never on a mention.
    [Theory]
    [InlineData("var vm = new SettingsViewModel(a, b, clock: PresentationClock.System);", 1)]
    [InlineData("SettingsViewModel Create() => new(a, b, ViewModels.PresentationClock . System);", 1)]
    [InlineData("var c = new PresentationClock(TimeProvider.System);", 1)]
    [InlineData("var c = new PresentationClock(new FakeTimeProvider()); // not PresentationClock.System", 0)]
    [InlineData("/* new PresentationClock(new FakeTimeProvider()) */ var c = PresentationClock.System;", 1)]
    [InlineData("var c = PresentationClock.System; // new PresentationClock(new FakeTimeProvider())", 1)]
    [InlineData("var s = \"PresentationClock.System\"; var c = TestClock.Fake();", 0)]
    [InlineData("var s = @\"TimeProvider.System \"\" quoted\"; var c = TestClock.Fake();", 0)]
    [InlineData("var s = $\"{PresentationClock.System}\"; var c = TestClock.Fake();", 1)] // a hole is code (Atlas C4)
    [InlineData("var clock = TestClock.Fake(); _ = $\"{clock = PresentationClock.System}\";", 1)]
    [InlineData("var s = $\"PresentationClock.System {{not a hole}}\";", 0)]
    [InlineData("var s = $@\"{TimeProvider.System} \"\" quoted\";", 1)]
    [InlineData("var s = \"\"\"\n PresentationClock.System\n \"\"\"; var c = TestClock.Fake();", 0)]
    [InlineData("var ch = '\"'; var c = PresentationClock.System;", 1)]
    [InlineData("var c = MyPresentationClock.System; var d = TimeProvider.SystemTime;", 0)]
    public void TheGuard_CountsCodeUses_IgnoringCommentsAndStrings(string source, int expected) =>
        Assert.Equal(expected, SystemClock.Matches(CodeOnly(source)).Count);

    /// <summary>Blanks comments and literals (keeping newlines, so line numbers stay true).</summary>
    internal static string CodeOnly(string source)
    {
        var output = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    output.Append(' ');
                    i++;
                }
            }
            else if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = Blank(source, output, i, end < 0 ? source.Length : end + 2);
            }
            else if (c == '"' && next == '"' && i + 2 < source.Length && source[i + 2] == '"')
            {
                var quotes = 0;
                while (i + quotes < source.Length && source[i + quotes] == '"')
                {
                    quotes++;
                }

                var fence = new string('"', quotes);
                var end = source.IndexOf(fence, i + quotes, StringComparison.Ordinal);
                i = Blank(source, output, i, end < 0 ? source.Length : end + quotes);
            }
            else if (c == '@' && next == '"' || (c == '$' || c == '@') && (next == '@' || next == '$') && i + 2 < source.Length && source[i + 2] == '"')
            {
                var start = i;
                i += c == '@' && next == '"' ? 2 : 3;
                while (i < source.Length && !(source[i] == '"' && (i + 1 >= source.Length || source[i + 1] != '"')))
                {
                    i += source[i] == '"' ? 2 : 1; // "" is an escaped quote in a verbatim literal
                }

                i = Blank(source, output, start, Math.Min(source.Length, i + 1), interpolated: c == '$' || next == '$');
            }
            else if (c == '"' || c == '$' && next == '"')
            {
                var start = i;
                i += c == '$' ? 2 : 1;
                while (i < source.Length && source[i] != '"' && source[i] != '\n')
                {
                    i += source[i] == '\\' ? 2 : 1;
                }

                i = Blank(source, output, start, Math.Min(source.Length, i + 1), interpolated: c == '$');
            }
            else if (c == '\'')
            {
                var start = i;
                i++;
                while (i < source.Length && source[i] != '\'' && source[i] != '\n')
                {
                    i += source[i] == '\\' ? 2 : 1;
                }

                i = Blank(source, output, start, Math.Min(source.Length, i + 1));
            }
            else
            {
                output.Append(c);
                i++;
            }
        }

        return output.ToString();
    }

    // Blanks [start, end); in an interpolated literal the holes ({...}, not {{) are kept as code.
    private static int Blank(string source, StringBuilder output, int start, int end, bool interpolated = false)
    {
        var depth = 0;
        for (var index = start; index < end; index++)
        {
            var ch = source[index];
            if (interpolated && depth == 0 && ch == '{' && index + 1 < end && source[index + 1] == '{')
            {
                output.Append("  ");
                index++;
                continue;
            }

            if (interpolated && ch == '{')
            {
                depth++;
                output.Append(depth == 1 ? ' ' : ch);
                continue;
            }

            if (interpolated && depth > 0 && ch == '}')
            {
                depth--;
                output.Append(depth == 0 ? ' ' : ch);
                continue;
            }

            output.Append(depth > 0 ? ch : ch == '\n' ? '\n' : ' ');
        }

        return end;
    }
}
