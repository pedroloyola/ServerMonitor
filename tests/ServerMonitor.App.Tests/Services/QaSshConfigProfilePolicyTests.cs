using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// The --qa-ssh-config guard. These tests compile and run in every configuration, so the Release
/// run proves the flag is ignored there and the harness type is not even compiled in.
/// </summary>
public sealed class QaSshConfigProfilePolicyTests
{
    private const string Fixture = @"C:\qa\fixtures\normal";

    [Theory]
    [InlineData("--qa-ssh-config", Fixture)]
    [InlineData("--qa-ssh-config=" + Fixture, null)]
    [InlineData("--QA-SSH-CONFIG", Fixture)]
    public void Release_IgnoresTheFlag(string flag, string? value)
    {
        var args = value is null ? new[] { "app.exe", flag } : new[] { "app.exe", flag, value };

        Assert.Null(QaSshConfigProfilePolicy.ResolveProfile(args, isDebugBuild: false));
    }

    [Theory]
    [InlineData("--qa-ssh-config", Fixture)]
    [InlineData("--qa-ssh-config=" + Fixture, null)]
    [InlineData("--QA-SSH-CONFIG", Fixture)]
    public void Debug_ResolvesTheFixtureProfile(string flag, string? value)
    {
        var args = value is null ? new[] { "app.exe", flag } : new[] { "app.exe", flag, value };

        Assert.Equal(Fixture, QaSshConfigProfilePolicy.ResolveProfile(args, isDebugBuild: true));
    }

    public static TheoryData<string[]> UnusableArguments { get; } = new()
    {
        new[] { "app.exe" },
        new[] { "app.exe", "--qa-ssh-config" },
        new[] { "app.exe", "--qa-ssh-config", "" },
        new[] { "app.exe", "--qa-ssh-config", @"relative\dir" },
        new[] { "app.exe", "--qa-ssh-config=" },
        new[] { "app.exe", "--qa-ssh-configs", Fixture }
    };

    [Theory]
    [MemberData(nameof(UnusableArguments))]
    public void Debug_WithoutAUsableFullyQualifiedDirectory_IsNotRequested(string[] args)
    {
        Assert.Null(QaSshConfigProfilePolicy.ResolveProfile(args, isDebugBuild: true));
    }

    [Fact]
    public void Harness_IsCompiledOnlyInDebug()
    {
        var harness = typeof(App).Assembly.GetType("ServerMonitor.App.Qa.QaSshConfigComposition");
#if DEBUG
        Assert.NotNull(harness);
#else
        Assert.Null(harness);
#endif
    }

    [Fact]
    public void Flag_BypassesSingleInstancingOnlyInDebug_LikeEveryQaHarness()
    {
        var args = new[] { "app.exe", "--qa-ssh-config", Fixture };

        Assert.Null(SingleInstancePolicy.ResolveInstanceKey(args, isDebugBuild: true));
        Assert.Equal("ServerMonitor", SingleInstancePolicy.ResolveInstanceKey(args, isDebugBuild: false));
    }
}
