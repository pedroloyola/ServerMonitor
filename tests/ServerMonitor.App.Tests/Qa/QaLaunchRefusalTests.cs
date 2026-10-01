using System.Text.RegularExpressions;
using ServerMonitor.App.Qa;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// UI.3 gate 1A (Boss decision 3): a <c>--qa-*</c> modifier used WITHOUT an isolated data harness would run the
/// production composition against real data, so <see cref="QaStartupIsolation.LaunchRefusal"/> refuses it before
/// anything is composed. Pure: argument lists only; directory arguments are a temporary SENTINEL that is never touched.
/// The last test keeps every launch line the QA/perf scripts print or run inside the allowed combinations.
/// </summary>
public sealed partial class QaLaunchRefusalTests
{
    private const string Exe = @"C:\fixture\ServerMonitor.App.exe";

    private static readonly string Sentinel = Path.Combine(
        Path.GetTempPath(), "ServerMonitor-QA-sentinel", $"{Environment.ProcessId}-{Guid.NewGuid():N}");

    public static TheoryData<string[]> ModifiersAlone => new()
    {
        { new[] { "--qa-ssh-config", Sentinel } },
        { new[] { "--qa-ssh-config=" + Sentinel } },
        { new[] { "--qa-ui-language", "pt-PT" } },
        { new[] { "--QA-UI-LANGUAGE=en-US" } },
        { new[] { "--qa-backup", "ok" } },
        { new[] { "--qa-proxyjump-dir=" + Sentinel } },
        { new[] { "--qa-backup", "rollback", "--qa-ui-language", "pt-BR" } },
        { new[] { "--qa-made-up-flag" } }
    };

    [Fact]
    public void AProductionLaunch_IsNotRefused()
    {
        Assert.Null(QaStartupIsolation.LaunchRefusal([Exe]));
        Assert.Null(QaStartupIsolation.LaunchRefusal([Exe, "--background"]));
    }

    [Theory]
    [MemberData(nameof(ModifiersAlone))]
    public void AModifierWithoutAnIsolatedHarness_IsRefused_NamingTheFlagAndTheHarnesses(string[] arguments)
    {
        var refusal = QaStartupIsolation.LaunchRefusal([Exe, .. arguments]);

        Assert.NotNull(refusal);
        Assert.StartsWith(arguments[0].Split('=')[0], refusal);
        Assert.All(QaStartupIsolation.HarnessFlags, harness => Assert.Contains(harness, refusal));
    }

    [Theory]
    [MemberData(nameof(ModifiersAlone))]
    public void TheSameModifier_WithAnyIsolatedHarness_IsAllowed(string[] arguments)
    {
        foreach (var harness in QaStartupIsolation.HarnessFlags)
        {
            Assert.Null(QaStartupIsolation.LaunchRefusal([Exe, harness, .. arguments]));
            Assert.Null(QaStartupIsolation.LaunchRefusal([Exe, .. arguments, harness.ToUpperInvariant()]));
        }
    }

    [Fact]
    public void TheCompactCountForm_CountsAsTheCompactHarness() =>
        Assert.Null(QaStartupIsolation.LaunchRefusal([Exe, "--qa-compact:12", "--qa-ui-language", "en-US"]));

    [Theory]
    [InlineData("--qa-components")]
    [InlineData("--qa-tokens")]
    [InlineData("--qa-gallery-page=forms")]
    public void GalleryFlags_AreLeftToTheExclusiveGalleryPolicy(string flag) =>
        // The gallery short-circuits first and refuses its own misuse (orphan options, foreign --qa-* flags).
        Assert.Null(QaStartupIsolation.LaunchRefusal([Exe, flag, "--qa-ui-language", "pt-PT"]));

    [Fact]
    public void HarnessFlags_AreExactlyTheDataHarnesses()
    {
        Assert.Equal(
            ["--qa-health", "--qa-discovery", "--qa-notifications", "--qa-compact", "--qa-history", "--qa-workloads",
                "--qa-store-screenshot", QaProxyJumpPolicy.LaunchFlag],
            QaStartupIsolation.HarnessFlags);
        Assert.DoesNotContain(QaSshConfigProfilePolicy.LaunchFlag, QaStartupIsolation.HarnessFlags);
        Assert.DoesNotContain(QaUiLanguagePolicy.LaunchFlag, QaStartupIsolation.HarnessFlags);
        Assert.DoesNotContain(QaBackupPolicy.LaunchFlag, QaStartupIsolation.HarnessFlags);
        Assert.DoesNotContain(QaProxyJumpPolicy.DirectoryFlag, QaStartupIsolation.HarnessFlags);
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
    /// Lexical: every line in tools/**/*.ps1 that launches the app or prints a launch command (it carries a --qa-* flag
    /// AND an executable reference, an ArgumentList or an $arguments value) must be an allowed combination.
    /// </summary>
    [Fact]
    public void EveryLaunchLineInTheQaAndPerfScripts_IsAnAllowedCombination()
    {
        var tools = Path.GetFullPath(Path.Combine(AppSourceTree.AppRoot, "..", "..", "tools"));
        var launches = Directory.EnumerateFiles(tools, "*.ps1", SearchOption.AllDirectories)
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

    [GeneratedRegex(@"--qa-[a-z-]+(:\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex QaFlag();

    [GeneratedRegex(@"\.exe\b|\$AppExe|ArgumentList|\$arguments\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex LaunchContext();

    [GeneratedRegex(@"//[^\r\n]*")]
    private static partial Regex LineComment();
}
