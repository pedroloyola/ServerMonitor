using ServerMonitor.App.Qa;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// UI.11 (Cortex §4, PH-1). The Debug-only --qa-reduced-motion modifier: exact, no value, at most once; refused alone
/// (a modifier is never a harness) and refused malformed; never on in Release; the production App path cannot pin it.
/// Compiled in every configuration (the isolation half only in Debug, where the QA harness exists).
/// </summary>
public sealed class QaReducedMotionPolicyTests
{
    private const string Exe = @"C:\fixture\ServerMonitor.App.exe";

    [Fact]
    public void Release_IsNeverOn()
    {
        Assert.False(QaReducedMotionPolicy.IsRequested([Exe, "--qa-health", QaReducedMotionPolicy.LaunchFlag], isDebugBuild: false));
    }

    [Fact]
    public void Debug_TheExactFlag_IsOn()
    {
        Assert.True(QaReducedMotionPolicy.IsRequested([Exe, "--qa-health", "--qa-reduced-motion"], isDebugBuild: true));
        Assert.False(QaReducedMotionPolicy.IsRequested([Exe, "--qa-health"], isDebugBuild: true));
    }

    public static TheoryData<string[]> Malformed => new()
    {
        new[] { Exe, "--qa-reduced-motion=1" },
        new[] { Exe, "--qa-reduced-motion=" },
        new[] { Exe, "--qa-reduced-motion=true" },
        new[] { Exe, "--QA-REDUCED-MOTION" },
        new[] { Exe, "--Qa-Reduced-Motion=on" },
        new[] { Exe, "--qa-reduced-motion", "--qa-reduced-motion" }
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_IsRefused_AndNeverOn(string[] args)
    {
        Assert.NotNull(QaReducedMotionPolicy.Refusal(args));
        Assert.False(QaReducedMotionPolicy.IsRequested(args, isDebugBuild: true));
    }

    [Fact]
    public void Absent_OrWellFormed_IsNotRefused()
    {
        Assert.Null(QaReducedMotionPolicy.Refusal([Exe]));
        Assert.Null(QaReducedMotionPolicy.Refusal([Exe, "--qa-history", "--qa-reduced-motion"]));
        Assert.Null(QaReducedMotionPolicy.Refusal([Exe, "--qa-reduced-motions"])); // another (unknown) switch, refused elsewhere
    }

#if DEBUG
    [Fact]
    public void Isolation_TheFlagIsAModifier_RefusedAlone_AllowedNextToAHarness()
    {
        Assert.Contains(QaReducedMotionPolicy.LaunchFlag, QaStartupIsolation.ModifierFlags);
        Assert.DoesNotContain(QaReducedMotionPolicy.LaunchFlag, QaStartupIsolation.HarnessFlags);
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, "--qa-reduced-motion"]));
        Assert.Null(QaStartupIsolation.LaunchRefusal([Exe, "--qa-history", "--qa-reduced-motion"]));
        Assert.False(QaStartupIsolation.IsHarnessLaunch([Exe, "--qa-reduced-motion"]));
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Isolation_MalformedNextToAHarness_IsRefused(string[] args)
    {
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([.. args, "--qa-history"]));
    }

    [Fact]
    public void Gallery_AcceptsTheExactFlag_AndRefusesItsMalformedForms()
    {
        var real = Path.Combine(Path.GetTempPath(), "sm-real-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(Path.GetTempPath(), "sm-gallery-" + Guid.NewGuid().ToString("N"));

        Assert.Null(QaGalleryPolicy.Resolve([Exe, "--qa-components", "--qa-reduced-motion"], true, real, output).RefusalReason);
        Assert.NotNull(QaGalleryPolicy.Resolve([Exe, "--qa-components", "--qa-reduced-motion=1"], true, real, output).RefusalReason);
        Assert.NotNull(QaGalleryPolicy.Resolve([Exe, "--qa-components", "--QA-REDUCED-MOTION"], true, real, output).RefusalReason);
    }
#endif

    /// <summary>
    /// The production source is installed with <c>forceReduced: false</c> in Release: every read of the QA flag in the App
    /// constructor sits inside an <c>#if DEBUG</c> branch, and the <c>#else</c> branch pins false.
    /// </summary>
    [Fact]
    public void TheAppConstructor_ReadsTheFlagOnlyInDebugBranches()
    {
        var lines = AppSourceTree.CodeWithoutComments("App.xaml.cs").Split('\n');
        var inDebug = new Stack<bool>();
        var reads = 0;
        var releaseInstall = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("#if DEBUG", StringComparison.Ordinal)) inDebug.Push(true);
            else if (line.StartsWith("#if", StringComparison.Ordinal)) inDebug.Push(false);
            else if (line.StartsWith("#else", StringComparison.Ordinal)) inDebug.Push(!inDebug.Pop());
            else if (line.StartsWith("#endif", StringComparison.Ordinal)) inDebug.Pop();
            else if (line.Contains("QaReducedMotionPolicy.IsRequested", StringComparison.Ordinal))
            {
                reads++;
                Assert.True(inDebug.Count > 0 && inDebug.Peek(), "QaReducedMotionPolicy read outside #if DEBUG: " + line);
            }
            else if (line.Contains("CreateSystemSource(forceReduced: false)", StringComparison.Ordinal) && inDebug.Count > 0 && !inDebug.Peek())
            {
                releaseInstall = true;
            }
        }

        Assert.Equal(1, reads); // the application branch (the gallery's own read lives in Debug-only Qa/Gallery code)
        Assert.True(releaseInstall, "the Release branch must install the system source with forceReduced: false");
    }
}
