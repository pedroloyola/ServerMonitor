#if DEBUG
using ServerMonitor.App.Qa;
#endif
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// REL.0D. The Debug-only --qa-system-culture modifier: one culture-shaped value, at most once; refused alone (a modifier
/// is never a harness) and refused malformed; never on in Release; read only inside #if DEBUG in the App.
/// </summary>
public sealed class QaSystemCulturePolicyTests
{
    private const string Exe = @"C:\fixture\ServerMonitor.App.exe";

    [Fact]
    public void Release_IsNeverOn() =>
        Assert.Null(QaSystemCulturePolicy.ResolveCulture([Exe, "--qa-overview", QaSystemCulturePolicy.LaunchFlag, "fr-FR"], isDebugBuild: false));

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en-GB")]
    [InlineData("pt-AO")]
    [InlineData("pt-MZ")]
    [InlineData("pt-CV")]
    [InlineData("pt-Latn-BR")]
    [InlineData("de")]
    public void Debug_AWellFormedCulture_IsRead(string culture)
    {
        Assert.Equal(culture, QaSystemCulturePolicy.ResolveCulture([Exe, "--qa-overview", "--qa-system-culture", culture], isDebugBuild: true));
        Assert.Equal(culture, QaSystemCulturePolicy.ResolveCulture([Exe, "--qa-overview", "--qa-system-culture=" + culture], isDebugBuild: true));
    }

    [Fact]
    public void Absent_IsNeitherReadNorRefused()
    {
        Assert.Null(QaSystemCulturePolicy.ResolveCulture([Exe, "--qa-overview"], isDebugBuild: true));
        Assert.Null(QaSystemCulturePolicy.Refusal([Exe, "--qa-overview"]));
    }

    public static TheoryData<string[]> Malformed => new()
    {
        new[] { Exe, "--qa-system-culture" },
        new[] { Exe, "--qa-system-culture=" },
        new[] { Exe, "--qa-system-culture", "--qa-overview" },
        new[] { Exe, "--qa-system-culture", "fr_FR" },
        new[] { Exe, "--qa-system-culture", "..\\x" },
        new[] { Exe, "--qa-system-culture", "f" },
        new[] { Exe, "--qa-system-culture", "fr-FR", "--qa-system-culture", "en-GB" }
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_IsRefused_AndNeverRead(string[] args)
    {
        Assert.NotNull(QaSystemCulturePolicy.Refusal(args));
        Assert.Null(QaSystemCulturePolicy.ResolveCulture(args, isDebugBuild: true));
    }

#if DEBUG
    [Fact]
    public void Isolation_TheFlagIsAModifier_RefusedAlone_AllowedNextToAHarness()
    {
        Assert.Contains(QaSystemCulturePolicy.LaunchFlag, QaStartupIsolation.ModifierFlags);
        Assert.DoesNotContain(QaSystemCulturePolicy.LaunchFlag, QaStartupIsolation.HarnessFlags);
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, "--qa-system-culture", "fr-FR"]));
        Assert.Null(QaStartupIsolation.LaunchRefusal([Exe, "--qa-overview", "--qa-backup", "ok", "--qa-ui-language", "pt-BR", "--qa-system-culture", "fr-FR"]));
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Isolation_MalformedNextToAHarness_IsRefused(string[] args) =>
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([.. args, "--qa-history"]));
#endif

    /// <summary>The App reads the flag only inside #if DEBUG (the composition class itself is excluded from Release).</summary>
    [Fact]
    public void TheApp_ReadsTheFlagOnlyInDebugBranches()
    {
        var lines = AppSourceTree.CodeWithoutComments("App.xaml.cs").Split('\n');
        var inDebug = new Stack<bool>();
        var reads = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("#if DEBUG", StringComparison.Ordinal)) inDebug.Push(true);
            else if (line.StartsWith("#if", StringComparison.Ordinal)) inDebug.Push(false);
            else if (line.StartsWith("#else", StringComparison.Ordinal)) inDebug.Push(!inDebug.Pop());
            else if (line.StartsWith("#endif", StringComparison.Ordinal)) inDebug.Pop();
            else if (line.Contains("QaSystemCultureComposition", StringComparison.Ordinal))
            {
                reads++;
                Assert.True(inDebug.Count > 0 && inDebug.Peek(), "QaSystemCultureComposition used outside #if DEBUG: " + line);
            }
        }

        Assert.True(reads > 0, "App.xaml.cs no longer composes --qa-system-culture");
    }
}
