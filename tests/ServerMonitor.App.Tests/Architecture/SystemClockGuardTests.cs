using System.Text;
using System.Text.RegularExpressions;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.5 fix round 4 (Boss, Atlas C3 finding 1). The clock of every notice / toast owner (SettingsViewModel,
/// ServersViewModel, TransientNoticeTimer) is a REQUIRED constructor parameter, so OMITTING it does not compile. This
/// guard covers the remaining bypass - passing the real clock explicitly: <c>PresentationClock.System</c> and
/// <c>TimeProvider.System</c> are banned from the App test project's CODE.
/// <para>
/// Tokenizer (documented; the test project has no Roslyn reference): before matching, comments (<c>//</c>, <c>/* */</c>)
/// and literals (regular, verbatim <c>@"…"</c>, interpolated <c>$"…"</c> including its holes, raw <c>"""…"""</c> and
/// char literals) are blanked, so a mention in a comment or a string never counts and never hides a real use next to it.
/// Member access tolerates whitespace around the dot; the type may be qualified (<c>ViewModels.PresentationClock</c>).
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
    [InlineData("var s = $\"{PresentationClock.System}\"; var c = TestClock.Fake();", 0)]
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

                i = Blank(source, output, start, Math.Min(source.Length, i + 1));
            }
            else if (c == '"' || c == '$' && next == '"')
            {
                var start = i;
                i += c == '$' ? 2 : 1;
                while (i < source.Length && source[i] != '"' && source[i] != '\n')
                {
                    i += source[i] == '\\' ? 2 : 1;
                }

                i = Blank(source, output, start, Math.Min(source.Length, i + 1));
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

    private static int Blank(string source, StringBuilder output, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            output.Append(source[index] == '\n' ? '\n' : ' ');
        }

        return end;
    }
}
