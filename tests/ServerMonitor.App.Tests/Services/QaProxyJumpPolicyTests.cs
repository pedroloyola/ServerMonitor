using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// The --qa-proxyjump guard. These tests compile and run in every configuration, so the Release run proves
/// the flag is ignored there and the harness type is not even compiled in.
/// </summary>
public sealed class QaProxyJumpPolicyTests
{
    private const string Directory = @"C:\qa\proxyjump";

    [Theory]
    [InlineData("--qa-proxyjump-dir", Directory)]
    [InlineData("--qa-proxyjump-dir=" + Directory, null)]
    [InlineData("--QA-PROXYJUMP-DIR", Directory)]
    public void Release_IgnoresBothFlags(string flag, string? value)
    {
        var args = value is null
            ? new[] { "app.exe", "--qa-proxyjump", flag }
            : new[] { "app.exe", "--qa-proxyjump", flag, value };

        Assert.False(QaProxyJumpPolicy.IsRequested(args, isDebugBuild: false));
        Assert.Null(QaProxyJumpPolicy.ResolveDirectory(args, isDebugBuild: false));
    }

    [Theory]
    [InlineData("--qa-proxyjump-dir", Directory)]
    [InlineData("--qa-proxyjump-dir=" + Directory, null)]
    [InlineData("--QA-PROXYJUMP-DIR", Directory)]
    public void Debug_ResolvesTheIsolatedDirectory(string flag, string? value)
    {
        var args = value is null
            ? new[] { "app.exe", "--qa-proxyjump", flag }
            : new[] { "app.exe", "--qa-proxyjump", flag, value };

        Assert.True(QaProxyJumpPolicy.IsRequested(args, isDebugBuild: true));
        Assert.Equal(Directory, QaProxyJumpPolicy.ResolveDirectory(args, isDebugBuild: true));
    }

    public static TheoryData<string[]> UnusableArguments { get; } = new()
    {
        new[] { "app.exe", "--qa-proxyjump" },
        new[] { "app.exe", "--qa-proxyjump", "--qa-proxyjump-dir" },
        new[] { "app.exe", "--qa-proxyjump", "--qa-proxyjump-dir", "" },
        new[] { "app.exe", "--qa-proxyjump", "--qa-proxyjump-dir", @"relative\dir" },
        new[] { "app.exe", "--qa-proxyjump", "--qa-proxyjump-dir=" },
        new[] { "app.exe", "--qa-proxyjump", "--qa-proxyjump-dirs", Directory }
    };

    [Theory]
    [MemberData(nameof(UnusableArguments))]
    public void Debug_WithoutAUsableFullyQualifiedDirectory_ResolvesNothing(string[] args)
    {
        Assert.Null(QaProxyJumpPolicy.ResolveDirectory(args, isDebugBuild: true));
    }

    [Fact]
    public void TheDirectoryFlagAlone_DoesNotRequestTheMode()
    {
        var args = new[] { "app.exe", "--qa-proxyjump-dir", Directory };

        Assert.False(QaProxyJumpPolicy.IsRequested(args, isDebugBuild: true));
    }

    [Fact]
    public void Harness_IsCompiledOnlyInDebug()
    {
        var harness = typeof(App).Assembly.GetType("ServerMonitor.App.Qa.QaProxyJumpComposition");
#if DEBUG
        Assert.NotNull(harness);
#else
        Assert.Null(harness);
#endif
    }

    [Fact]
    public void Flag_BypassesSingleInstancingOnlyInDebug_LikeEveryQaHarness()
    {
        var args = new[] { "app.exe", "--qa-proxyjump", "--qa-proxyjump-dir", Directory };

        Assert.Null(SingleInstancePolicy.ResolveInstanceKey(args, isDebugBuild: true));
        Assert.Equal("ServerMonitor", SingleInstancePolicy.ResolveInstanceKey(args, isDebugBuild: false));
    }
}
