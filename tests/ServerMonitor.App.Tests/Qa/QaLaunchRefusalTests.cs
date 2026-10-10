using System.Text.RegularExpressions;
using ServerMonitor.App.Qa;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// UI.3 gate 1A (Boss decision 3, Vigil M-1A-1). A launch is a QA harness only when an argument is EXACTLY one of the
/// harness flags - the one parser the composition root's qaMode also uses - and every other <c>--qa*</c> argument is an
/// exact harness flag or a well-formed modifier. Anything else (a modifier alone, an unknown switch, a malformed,
/// value-glued or differently-cased flag) is refused before anything is composed. Pure: argument lists only; directory
/// arguments are a temporary SENTINEL that is never touched; the app is never launched.
/// </summary>
public sealed partial class QaLaunchRefusalTests
{
    private const string Exe = @"C:\fixture\ServerMonitor.App.exe";

    private static readonly string Sentinel = Path.Combine(
        Path.GetTempPath(), "ServerMonitor-QA-sentinel", $"{Environment.ProcessId}-{Guid.NewGuid():N}");

    /// <summary>Well-formed modifiers: refused alone, allowed next to an exact harness.</summary>
    public static TheoryData<string[]> ValidModifiers => new()
    {
        { new[] { "--qa-ssh-config", Sentinel } },
        { new[] { "--qa-ssh-config=" + Sentinel } },
        { new[] { "--qa-ui-language", "pt-PT" } },
        { new[] { "--qa-ui-language=en-US" } },
        { new[] { "--qa-backup", "ok" } },
        { new[] { "--qa-proxyjump-dir=" + Sentinel } },
        { new[] { "--qa-backup", "rollback", "--qa-ui-language", "pt-BR" } }
    };

    /// <summary>
    /// Vigil's probe forms and the general rule: any argument starting with --qa that is not exactly a recognised harness
    /// or a well-formed modifier. Each is refused alone, next to a modifier, AND next to a valid harness.
    /// </summary>
    public static TheoryData<string> MalformedSwitches => new(Malformed);

    private static readonly string[] Malformed =
    [
        "--qa-health=1",
        "--qa-health:x",
        "--qa-history=",
        "--qa-proxyjump=on",
        "--qa-compact=8",
        "--qa-compact:",
        "--qa-compact:abc",
        "--qa-compact:12",
        "--qa-compact-scenario:figma",
        "--QA-HEALTH",
        "--Qa-Health",
        "--qa-health1",
        "--qahealth",
        "--qa",
        "--qa-",
        "--QA-UI-LANGUAGE=en-US",
        "--qa-ui-language=",
        "--qa-backup:ok",
        "--qa-made-up-flag",
        "--qa-gallery-page=forms",
        "--qa-components=1"
    ];

    [Fact]
    public void AProductionLaunch_IsNotRefused()
    {
        Assert.Null(QaStartupIsolation.LaunchRefusal([Exe]));
        Assert.Null(QaStartupIsolation.LaunchRefusal([Exe, "--background"]));
    }

    [Theory]
    [MemberData(nameof(ValidModifiers))]
    public void AModifierWithoutAnIsolatedHarness_IsRefused_NamingTheFlagAndTheHarnesses(string[] arguments)
    {
        var refusal = QaStartupIsolation.LaunchRefusal([Exe, .. arguments]);

        Assert.NotNull(refusal);
        Assert.StartsWith(arguments[0].Split('=')[0], refusal);
        Assert.All(QaStartupIsolation.HarnessFlags, harness => Assert.Contains(harness, refusal));
    }

    [Theory]
    [MemberData(nameof(ValidModifiers))]
    public void TheSameModifier_WithAnyExactHarness_IsAllowed_AndQaModeIsOn(string[] arguments)
    {
        foreach (var harness in QaStartupIsolation.HarnessFlags)
        {
            // UI.5 (Boss B2 answer 5): the overview harness always carries the backup doubles; UI.7 B-23: the editor too.
            string[] extra = (harness == QaOverviewComposition.LaunchFlag || harness == QaEditorComposition.LaunchFlag) && !arguments.Any(a => a.StartsWith("--qa-backup", StringComparison.Ordinal))
                ? ["--qa-backup", "ok"]
                : [];
            string[] before = [Exe, harness, .. arguments, .. extra];
            string[] after = [Exe, .. arguments, .. extra, harness];
            Assert.Null(QaStartupIsolation.LaunchRefusal(before));
            Assert.Null(QaStartupIsolation.LaunchRefusal(after));
            Assert.True(QaStartupIsolation.IsHarnessLaunch(before));
            Assert.True(QaStartupIsolation.IsHarnessLaunch(after));
        }
    }

    [Theory]
    [MemberData(nameof(MalformedSwitches))]
    public void AMalformedOrUnknownQaSwitch_IsRefused_AloneWithAModifierAndEvenNextToAValidHarness(string malformed)
    {
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, malformed]));
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, malformed, "--qa-backup", "ok"]));
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, "--qa-ui-language", "pt-PT", malformed]));
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, "--qa-health", malformed]));
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, malformed, "--qa-proxyjump", "--qa-proxyjump-dir=" + Sentinel]));
    }

    [Theory]
    [MemberData(nameof(MalformedSwitches))]
    public void AMalformedSwitch_IsNeverAHarnessForTheCompositionRoot(string malformed)
    {
        // The same parser decides qaMode: what the refusal does not accept as a harness never selects a QA composition.
        Assert.False(QaStartupIsolation.IsHarnessArgument(malformed));
        Assert.False(QaStartupIsolation.IsHarnessLaunch([Exe, malformed, "--qa-backup", "ok"]));
    }

    /// <summary>
    /// The invariant Vigil's probe broke: whenever the refusal lets a launch with --qa* arguments through, either the
    /// exclusive gallery owns it or qaMode (same parser) is on - never the production composition.
    /// </summary>
    [Fact]
    public void WhateverTheRefusalAllows_SelectsAQaComposition()
    {
        var corpus = QaStartupIsolation.HarnessFlags
            .Concat(Malformed)
            .Concat(["--qa-ssh-config", "--qa-ui-language", "--qa-backup", "--qa-proxyjump-dir=" + Sentinel, "pt-PT", "ok", "--qa-compact:12"])
            .ToArray();

        foreach (var first in corpus)
        {
            foreach (var second in corpus)
            {
                string[] arguments = [Exe, first, second];
                if (QaStartupIsolation.LaunchRefusal(arguments) is null && arguments.Any(QaStartupIsolation.IsQaLike))
                {
                    Assert.True(QaStartupIsolation.IsHarnessLaunch(arguments), string.Join(' ', arguments));
                }
            }
        }
    }

    /// <summary>
    /// UI.8 §3 retired the one harness value form, <c>--qa-compact:&lt;digits&gt;</c> (it silently clamped to 0-40): every
    /// harness flag is now exact, and the compact harness takes a named scenario through its own modifier.
    /// </summary>
    [Fact]
    public void NoHarnessFlagCarriesAValue_TheCompactCountFormIsRetired()
    {
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, "--qa-compact:12", "--qa-ui-language", "en-US"]));
        Assert.False(QaStartupIsolation.IsHarnessArgument("--qa-compact:12"));
        Assert.False(QaStartupIsolation.IsHarnessArgument("--qa-compact=12"));
        Assert.True(QaStartupIsolation.IsHarnessArgument("--qa-compact"));
        Assert.Null(QaStartupIsolation.LaunchRefusal([Exe, "--qa-compact", "--qa-compact-scenario", "n20", "--qa-ui-language", "en-US"]));
    }

    [Theory]
    [InlineData("--qa-components")]
    [InlineData("--qa-tokens")]
    public void ExactGalleryFlags_AreLeftToTheExclusiveGalleryPolicy(string flag) =>
        // The gallery short-circuits before this runs and refuses its own misuse (orphan options, foreign --qa-* flags).
        Assert.Null(QaStartupIsolation.LaunchRefusal([Exe, flag, "--qa-gallery-page", "forms"]));

    [Fact]
    public void HarnessAndModifierFlags_AreExactlyTheDocumentedOnes()
    {
        Assert.Equal(
            ["--qa-health", "--qa-discovery", "--qa-notifications", "--qa-compact", "--qa-history", "--qa-workloads",
                "--qa-store-screenshot", QaProxyJumpPolicy.LaunchFlag, "--qa-overview", "--qa-editor"],
            QaStartupIsolation.HarnessFlags);
        Assert.Equal(
            [QaSshConfigProfilePolicy.LaunchFlag, QaUiLanguagePolicy.LaunchFlag, QaBackupPolicy.LaunchFlag, QaProxyJumpPolicy.DirectoryFlag,
                "--qa-overview-scenario", "--qa-start", "--qa-activation", "--qa-editor-seed", "--qa-editor-ssh", "--qa-editor-save",
                "--qa-compact-scenario", "--qa-compact-start", "--qa-compact-ticker", QaReducedMotionPolicy.LaunchFlag],
            QaStartupIsolation.ModifierFlags);
        Assert.True(QaProxyJumpPolicy.IsRequested([Exe, QaProxyJumpPolicy.LaunchFlag], isDebugBuild: true));
    }

    /// <summary>The refusal runs in the App constructor after the gallery short-circuit and BEFORE the host is composed.</summary>
    [Fact]
    public void TheRefusalPrecedesTheHostComposition_InTheAppConstructor()
    {
        // Comments stripped: a commented-out call is not a call (counterproof M2 caught the first version of this test).
        var source = LineComment().Replace(File.ReadAllText(AppSourceTree.Full("App.xaml.cs")), string.Empty);
        var gallery = source.IndexOf("Qa.Gallery.QaGalleryComposition.IsRequested()", StringComparison.Ordinal);
        var refusal = source.IndexOf("Qa.QaStartupIsolation.RefuseUnisolatedLaunch();", StringComparison.Ordinal);
        var host = source.IndexOf(".CreateDefaultBuilder()", StringComparison.Ordinal);

        Assert.True(gallery >= 0 && refusal >= 0 && host >= 0, "anchor not found in App.xaml.cs");
        Assert.True(gallery < refusal && refusal < host, "RefuseUnisolatedLaunch must sit between the gallery branch and the host build");
    }

    /// <summary>
    /// Lexical: every line in tools/**/*.ps1 that launches the app or prints a launch command (it carries a --qa* switch
    /// AND an executable reference, an ArgumentList or an $arguments value) must be an allowed combination.
    /// </summary>
    [Fact]
    public void EveryLaunchLineInTheQaAndPerfScripts_IsAnAllowedCombination()
    {
        var tools = Path.GetFullPath(Path.Combine(AppSourceTree.AppRoot, "..", "..", "tools"));
        var launches = Directory.EnumerateFiles(tools, "*.ps1", SearchOption.AllDirectories)
            // The launcher itself holds no launch line, only refusal messages (it is covered by StartQaAppScriptTests).
            .Where(file => !string.Equals(Path.GetFileName(file), "Start-QaApp.ps1", StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, index, line)))
            .Where(entry => QaFlag().IsMatch(entry.line) && LaunchContext().IsMatch(entry.line))
            .ToList();

        Assert.NotEmpty(launches);
        Assert.All(launches, entry =>
        {
            var flags = QaFlag().Matches(entry.line).Select(match => match.Value).ToArray();
            var refusal = QaStartupIsolation.LaunchRefusal([Exe, .. flags]);
            Assert.True(refusal is null, $"{Path.GetRelativePath(tools, entry.file)}:{entry.index + 1}: {refusal}");
        });
    }

    /// <summary>
    /// Cortex B1 M-2 / Boss B2 answer 5: a tools/** launch line for the overview harness WITHOUT the backup doubles would be
    /// refused by the same lexical pipeline as the real lines above (here the 'mixed' scenario); with them it is allowed.
    /// </summary>
    [Theory]
    [InlineData("& tools/qa/Start-QaApp.ps1 -Exe $AppExe -Arguments '--qa-overview', '--qa-overview-scenario', 'mixed'", false)]
    [InlineData("$arguments = @('--qa-overview', '--qa-overview-scenario=mixed')", false)]
    [InlineData("& tools/qa/Start-QaApp.ps1 -Exe $AppExe -Arguments '--qa-overview', '--qa-overview-scenario', 'mixed', '--qa-backup', 'ok'", true)]
    [InlineData("$arguments = @('--qa-overview', '--qa-overview-scenario=mixed', '--qa-backup=ok')", true)]
    public void AnOverviewLaunchLine_IsAllowedOnlyWithTheBackupDoubles(string line, bool allowed)
    {
        Assert.Matches(LaunchContext(), line);
        // The quoted PowerShell tokens are the arguments the app receives ('mixed' and 'ok' are separate tokens).
        var tokens = Regex.Matches(line, "'([^']+)'").Select(match => match.Groups[1].Value).ToArray();
        var arguments = tokens;

        var refusal = QaStartupIsolation.LaunchRefusal([Exe, .. arguments]);

        Assert.Equal(allowed, refusal is null);
        if (!allowed)
        {
            Assert.Contains(QaBackupPolicy.LaunchFlag, refusal!, StringComparison.Ordinal);
        }
    }

    // A flag with its glued value when it has one ('--qa-proxyjump-dir=`"...' keeps '--qa-proxyjump-dir=' + value).
    [GeneratedRegex(@"--qa[a-z-]*(:\d+)?(=[^\s""'`]+|=`""[^`]*`"")?", RegexOptions.IgnoreCase)]
    private static partial Regex QaFlag();

    [GeneratedRegex(@"\.exe\b|\$AppExe|ArgumentList|\$arguments\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex LaunchContext();

    [GeneratedRegex(@"//[^\r\n]*")]
    private static partial Regex LineComment();
}
